using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PartyTime.Audio;
using PartyTime.Interop;
using PartyTime.Settings;

namespace PartyTime.Visualization;

internal sealed class Overlay : IDisposable
{
    private static readonly long IdleInterval = Stopwatch.Frequency / 20;
    private readonly SystemAudio _audio;
    private readonly Window _owner;
    private CompositionClock? _clock;
    private Display[] _displays = [];
    private readonly Scene _scene = new();
    private readonly ScreenSamplePlayback _samplePlayback = new();
    private VisualizerSettings _settings = new();
    private long _lastRender;
    private bool _captureAvailable;
    private Display? PrimaryDisplay => _primaryDisplay;
    private Display? _primaryDisplay;
    public Color[]? ScreenColors => PrimaryDisplay?.Samples?.Colors ?? PrimaryDisplay?.Colors?.Latest;
    public bool CanSampleScreen => _captureAvailable;
    public ScreenSampleFrame? AcquirePrimarySample() => PrimaryDisplay?.Samples?.AcquireFrame(_settings.SampleBlend);
    public ScreenSampleRequest SampleRequest => _samplePlayback.Request;
    public string ScreenSampleStatus => !_captureAvailable ? DesktopCapture.UnavailableReason
        : _displays.Select(d => d.Samples?.Error).FirstOrDefault(error => error != null)
            ?? (PrimaryDisplay?.Samples?.HasFrame != true ? "Waiting for a beat… Play audio in the selected range." : "Live sample · screenshots stay in memory.");

    public Overlay(SystemAudio audio, Window owner)
    {
        _audio = audio;
        _owner = owner;
        try { RebuildScreens(); }
        catch { Dispose(); throw; }
    }

    public void ApplySettings(VisualizerSettings settings)
    {
        static bool ColorsEnabled(VisualizerSettings value) => value.ColorSource == ColorSource.Screen || value.ColorSource == ColorSource.Mix && value.ScreenBlend > 0;
        bool recapture = ColorsEnabled(settings) != ColorsEnabled(_settings) || settings.ScreenSamplesEnabled != _settings.ScreenSamplesEnabled;
        bool pacing = settings.FrameRate != _settings.FrameRate || settings.CursorTrailEnabled != _settings.CursorTrailEnabled;
        _settings = settings;
        _scene.ApplySettings(settings);
        _samplePlayback.ApplySettings(settings);
        if (pacing) ConfigureFramePacing();
        else { long now = Stopwatch.GetTimestamp(); foreach (var display in _displays) display.NextFrame = now; }
        if (recapture) ConfigureSampler();
    }

    private void ConfigureSampler()
    {
        bool colors = _settings.ColorSource == ColorSource.Screen || (_settings.ColorSource == ColorSource.Mix && _settings.ScreenBlend > 0);
        bool capture = colors || _settings.ScreenSamplesEnabled;
        _captureAvailable = DesktopCapture.IsSupported;
        foreach (var d in _displays) _captureAvailable &= d.Window.SetCaptureExcluded(capture);
        foreach (var d in _displays)
        {
            d.Window.Surface.ShowScene(null);
            d.Window.Surface.ClearScreenSample();
            d.Colors?.Dispose(); d.Colors = null;
            d.Samples?.Dispose(); d.Samples = null;
            if (!_captureAvailable || !capture) continue;
            bool primaryColors = colors && ReferenceEquals(d, PrimaryDisplay);
            if (_settings.ScreenSamplesEnabled) d.Samples = new BeatScreenSampler(d.Screen, sampleColors: primaryColors);
            else if (primaryColors) d.Colors = new ScreenColorSampler(d.Screen);
        }
    }

    public void RebuildScreens()
    {
        _clock?.Dispose(); _clock = null;
        _scene.ClearCursorTrail();
        CloseDisplays();
        var displays = new List<Display>();
        try
        {
            foreach (var screen in _owner.Screens.All)
            {
                var window = new OverlayWindow();
                displays.Add(new Display(window, screen));
                window.Open(screen);
            }
            _displays = displays.ToArray();
            _primaryDisplay = _displays.FirstOrDefault(d => d.Screen.IsPrimary) ?? _displays.FirstOrDefault();
            ConfigureSampler();
            ConfigureFramePacing();
            var fastest = _displays.OrderByDescending(d => d.RefreshRate).FirstOrDefault();
            if (fastest != null) _clock = new CompositionClock(fastest.Window, Render);
        }
        catch
        {
            foreach (var d in displays) d.Window.Close();
            throw;
        }
    }

    private void ConfigureFramePacing()
    {
        var rates = OperatingSystem.IsWindows() ? DisplayRefreshRates.Read() : null;
        long now = Stopwatch.GetTimestamp();
        foreach (var d in _displays)
        {
            double refresh = ScreenRefreshRate.Read(d.Screen, rates);
            d.RefreshRate = refresh;
            d.FramePeriod = FramePacing.Period(_settings.FrameRate == OverlayFrameRate.MatchDisplay ? refresh : (int)_settings.FrameRate);
            d.CursorPeriod = FramePacing.Period(FramePacing.CursorRate(refresh));
            d.NextFrame = d.NextCursorFrame = now;
        }
    }

    private void Render()
    {
        long now = Stopwatch.GetTimestamp();
        const double toleranceSeconds = 0.001;
        long tolerance = (long)(Stopwatch.Frequency * toleranceSeconds);
        double elapsed = _lastRender != 0 ? Math.Min(Stopwatch.GetElapsedTime(_lastRender, now).TotalSeconds, 0.2) : 1.0 / 30;
        _lastRender = now;
        _scene.UpdatePointer(_settings.CursorTrailEnabled ? Desktop.GetPointer() : null, now);
        bool visible = _scene.Update(_audio.Latest, now, elapsed, ScreenColors);
        _samplePlayback.Update(_audio.Latest, now);
        foreach (var d in _displays)
        {
            d.Samples?.Update(_samplePlayback.Request);
            bool due = now + tolerance >= d.NextFrame;
            bool cursorDue = _settings.CursorTrailEnabled && now + tolerance >= d.NextCursorFrame;
            bool cursor = visible && _scene.CursorIntersects(new Rect(d.Screen.Bounds.X, d.Screen.Bounds.Y, d.Screen.Bounds.Width, d.Screen.Bounds.Height), d.Screen.Scaling);
            double opacity = d.Samples?.HasFrame == true ? _samplePlayback.Request.Opacity(now) : 0;
            if (due) d.NextFrame = FramePacing.Advance(d.NextFrame, Math.Max(now, d.NextFrame), _scene.BaseVisible || opacity > 0 ? d.FramePeriod : IdleInterval);
            if (cursorDue) d.NextCursorFrame = FramePacing.Advance(d.NextCursorFrame, Math.Max(now, d.NextCursorFrame), d.CursorPeriod);
            if (due || cursorDue && (cursor || d.CursorVisible) || !_settings.CursorTrailEnabled && d.CursorVisible)
            {
                d.Window.Surface.ShowScene(visible ? _scene : null, due, cursor);
                d.CursorVisible = cursor;
            }
            // Screen fades/zoom follow display animation, independently of the music FPS.
            if (opacity > 0.005 || d.SampleVisible)
                d.Window.Surface.ShowScreenSample(opacity > 0.005 ? d.Samples?.AcquireFrame(_settings.SampleBlend) : null, _samplePlayback.Request, now);
            d.SampleVisible = opacity > 0.005;
        }
    }

    public void Dispose() { _clock?.Dispose(); _clock = null; CloseDisplays(); }
    private void CloseDisplays()
    {
        foreach (var d in _displays) { d.Colors?.Dispose(); d.Samples?.Dispose(); d.Window.Close(); }
        _displays = [];
        _primaryDisplay = null;
    }
    private sealed class Display(OverlayWindow window, Screen screen)
    {
        public OverlayWindow Window { get; } = window;
        public Screen Screen { get; } = screen;
        public long FramePeriod, CursorPeriod, NextFrame, NextCursorFrame;
        public double RefreshRate;
        public bool CursorVisible, SampleVisible;
        public BeatScreenSampler? Samples;
        public ScreenColorSampler? Colors;
    }
}
