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
    private readonly DispatcherTimer _clock;
    private Display[] _displays = [];
    private readonly Scene _scene = new();
    private readonly ScreenSamplePlayback _samplePlayback = new();
    private VisualizerSettings _settings = new();
    private long _lastRender;
    private bool _captureAvailable;
    public Color[]? ScreenColors => _displays.FirstOrDefault(d => d.Screen.IsPrimary)?.Colors?.Latest ?? _displays.FirstOrDefault()?.Colors?.Latest;
    public bool CanSampleScreen => _captureAvailable;
    public Bitmap? ScreenSamplePreview => (_displays.FirstOrDefault(d => d.Screen.IsPrimary) ?? _displays.FirstOrDefault())?.Samples?.Latest;
    public string ScreenSampleStatus => !_captureAvailable ? DesktopCapture.UnavailableReason
        : _displays.Select(d => d.Samples?.Error).FirstOrDefault(error => error != null)
            ?? (ScreenSamplePreview == null ? "Waiting for a beat… Play audio in the selected range." : "Live sample · screenshots stay in memory.");

    public Overlay(SystemAudio audio, Window owner)
    {
        _audio = audio;
        _owner = owner;
        _clock = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 120) };
        _clock.Tick += (_, _) => Render();
        try { RebuildScreens(); _clock.Start(); }
        catch { Dispose(); throw; }
    }

    public void ApplySettings(VisualizerSettings settings)
    {
        bool recapture = settings.ColorSource != _settings.ColorSource || settings.ScreenBlend != _settings.ScreenBlend
            || settings.ScreenSamplesEnabled != _settings.ScreenSamplesEnabled;
        _settings = settings;
        _scene.ApplySettings(settings);
        _samplePlayback.ApplySettings(settings);
        ConfigureFramePacing();
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
            d.Colors?.Dispose(); d.Colors = null;
            d.Samples?.Dispose(); d.Samples = null;
            if (!_captureAvailable || !capture) continue;
            if (colors && (d.Screen.IsPrimary || _displays.Length == 1)) d.Colors = new ScreenColorSampler(d.Screen);
            if (_settings.ScreenSamplesEnabled) d.Samples = new BeatScreenSampler(d.Screen);
        }
    }

    public void RebuildScreens()
    {
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
            ConfigureSampler();
            ConfigureFramePacing();
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
            d.FramePeriod = FramePacing.Period(_settings.FrameRate == OverlayFrameRate.MatchDisplay ? refresh : (int)_settings.FrameRate);
            d.CursorPeriod = FramePacing.Period(FramePacing.CursorRate(refresh));
            d.NextFrame = d.NextCursorFrame = now;
        }
        _clock.Interval = TimeSpan.FromMilliseconds(1);
    }

    private void Render()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = _lastRender != 0 ? Math.Min(Stopwatch.GetElapsedTime(_lastRender, now).TotalSeconds, 0.2) : 1.0 / 30;
        _lastRender = now;
        _scene.UpdatePointer(_settings.CursorTrailEnabled ? Desktop.GetPointer() : null, now);
        bool visible = _scene.Update(_audio.Latest, now, elapsed, ScreenColors);
        _samplePlayback.Update(_audio.Latest, now);
        foreach (var d in _displays)
        {
            d.Samples?.Update(_samplePlayback.Request);
            bool due = now >= d.NextFrame;
            bool cursorDue = _settings.CursorTrailEnabled && now >= d.NextCursorFrame;
            bool cursor = visible && _scene.CursorIntersects(new Rect(d.Screen.Bounds.X, d.Screen.Bounds.Y, d.Screen.Bounds.Width, d.Screen.Bounds.Height), d.Screen.Scaling);
            var sample = d.Samples?.Latest;
            double opacity = sample == null ? 0 : _samplePlayback.Request.Opacity(now);
            if (due) d.NextFrame = FramePacing.Advance(d.NextFrame, now, _scene.BaseVisible || opacity > 0 ? d.FramePeriod : IdleInterval);
            if (cursorDue) d.NextCursorFrame = FramePacing.Advance(d.NextCursorFrame, now, d.CursorPeriod);
            if (due || cursorDue && (cursor || d.CursorVisible) || !_settings.CursorTrailEnabled && d.CursorVisible)
            {
                d.Window.Surface.ShowScene(visible ? _scene : null, due, cursor, sample, opacity);
                d.CursorVisible = cursor;
            }
        }
        long next = _displays.Length == 0 ? now + IdleInterval : _displays.Min(d =>
            _settings.CursorTrailEnabled ? Math.Min(d.NextFrame, d.NextCursorFrame) : d.NextFrame);
        _clock.Interval = TimeSpan.FromSeconds(Math.Clamp((next - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency, 0.001, 0.05));

    }

    public void Dispose() { _clock.Stop(); CloseDisplays(); }
    private void CloseDisplays()
    {
        foreach (var d in _displays) { d.Colors?.Dispose(); d.Samples?.Dispose(); d.Window.Close(); }
        _displays = [];
    }
    private sealed class Display(OverlayWindow window, Screen screen)
    {
        public OverlayWindow Window { get; } = window;
        public Screen Screen { get; } = screen;
        public long FramePeriod, CursorPeriod, NextFrame, NextCursorFrame;
        public bool CursorVisible;
        public BeatScreenSampler? Samples;
        public ScreenColorSampler? Colors;
    }
}
