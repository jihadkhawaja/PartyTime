using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media;
using PartyTime.Audio;
using PartyTime.Interop;
using PartyTime.Settings;
using SharpGen.Runtime;
using Screen = System.Windows.Forms.Screen;

namespace PartyTime.Visualization;

internal sealed class Overlay : IDisposable
{
    private static readonly long IdleInterval = Stopwatch.Frequency / 20;
    private readonly SystemAudio _audio;
    private readonly OverlayClock _clock;
    private Display[] _displays = [];
    private readonly Scene _scene = new();
    private ScreenColorSampler? _sampler;
    private GpuOverlayRenderer? _gpu;
    private OverlayFrameRate _frameRate = OverlayFrameRate.Fps30;
    private long _lastRender;
    private bool _useScreenColors;

    public Overlay(SystemAudio audio)
    {
        _audio = audio;
        _clock = new OverlayClock(Render);
        try
        {
            RebuildScreens();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Color[]? ScreenColors => _sampler?.Latest;
    public bool CanSampleScreen => _sampler != null;

    public void ApplySettings(VisualizerSettings settings)
    {
        _scene.ApplySettings(settings);
        if (settings.FrameRate != _frameRate)
        {
            _frameRate = settings.FrameRate;
            ConfigureFramePacing();
        }
        bool useScreenColors = settings.ColorSource == ColorSource.Screen
            || (settings.ColorSource == ColorSource.Mix && settings.ScreenBlend > 0);
        if (useScreenColors != _useScreenColors)
        {
            _useScreenColors = useScreenColors;
            ConfigureSampler();
        }
    }

    private void ConfigureSampler()
    {
        _sampler?.Dispose();
        _sampler = null;
        bool canSample = _useScreenColors && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
        foreach (var display in _displays)
            canSample &= display.Window.SetCaptureExcluded(_useScreenColors);

        if (canSample && Screen.PrimaryScreen is { } screen)
        {
            _sampler = new ScreenColorSampler(screen.Bounds);
        }
        else if (_useScreenColors)
        {
            foreach (var display in _displays)
                display.Window.SetCaptureExcluded(false);
        }
    }

    public void RebuildScreens()
    {
        _gpu?.Dispose();
        _gpu = null;
        foreach (var display in _displays)
            display.Window.Close();

        var screens = Screen.AllScreens;
        var displays = new List<Display>(screens.Length);
        try
        {
            foreach (var screen in screens)
            {
                var window = new OverlayWindow();
                displays.Add(new Display(window, screen));
                window.Open(screen.Bounds);
            }
            _displays = displays.ToArray();
            try
            {
                _gpu = new GpuOverlayRenderer(_displays.Select(display => display.Window).ToArray(),
                    screens.Select(screen => screen.Bounds).ToArray());
            }
            catch (SharpGenException ex)
            {
                Debug.WriteLine(ex);
            }
            if (_useScreenColors)
                ConfigureSampler();
            ConfigureFramePacing();
        }
        catch
        {
            _gpu?.Dispose();
            _gpu = null;
            foreach (var display in displays)
                display.Window.Close();
            _displays = [];
            throw;
        }
    }

    public void Dispose()
    {
        _clock.Dispose();
        _gpu?.Dispose();
        _gpu = null;
        _sampler?.Dispose();
        foreach (var display in _displays)
            display.Window.Close();
        _displays = [];
    }

    private void ConfigureFramePacing()
    {
        var refreshRates = _frameRate == OverlayFrameRate.MatchDisplay ? DisplayRefreshRates.Read() : null;
        long now = Stopwatch.GetTimestamp();
        foreach (var display in _displays)
        {
            double rate = _frameRate == OverlayFrameRate.MatchDisplay
                ? refreshRates!.GetValueOrDefault(display.Screen.DeviceName, (int)OverlayFrameRate.Fps30)
                : (int)_frameRate;
            display.FramePeriod = Math.Max(1, (long)Math.Round(Stopwatch.Frequency / rate));
            display.NextFrame = now;
        }
        _clock.Schedule(now);
    }

    private void Render()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = _lastRender != 0
            ? Math.Min(Stopwatch.GetElapsedTime(_lastRender, now).TotalSeconds, 0.2)
            : 1.0 / (int)OverlayFrameRate.Fps30;
        _lastRender = now;

        bool visible = _scene.Update(_audio.Latest, now, elapsed, ScreenColors);
        Scene? scene = visible ? _scene : null;
        long nextFrame;
        try
        {
            nextFrame = DrawFrames(scene, now);
        }
        catch (SharpGenException ex) when (_gpu != null)
        {
            Debug.WriteLine(ex);
            _gpu.Dispose();
            _gpu = null;
            foreach (var display in _displays)
                display.NextFrame = now;
            nextFrame = DrawFrames(scene, now);
        }
        _clock.Schedule(nextFrame);
    }

    private long DrawFrames(Scene? scene, long now)
    {
        long nextFrame = long.MaxValue;
        for (int i = 0; i < _displays.Length; i++)
        {
            var display = _displays[i];
            if (scene is null || now >= display.NextFrame)
            {
                // Keep each display's cadence and skip missed frames when rendering runs late.
                display.NextFrame = scene is null ? now + IdleInterval
                    : display.NextFrame + ((now - display.NextFrame) / display.FramePeriod + 1) * display.FramePeriod;
                if (_gpu != null)
                    _gpu.Draw(i, scene);
                else
                    display.Window.Surface.ShowScene(scene);
            }
            nextFrame = Math.Min(nextFrame, display.NextFrame);
        }
        return _displays.Length == 0 ? now + IdleInterval : nextFrame;
    }

    private sealed class Display(OverlayWindow window, Screen screen)
    {
        public OverlayWindow Window { get; } = window;
        public Screen Screen { get; } = screen;
        public long FramePeriod { get; set; }
        public long NextFrame { get; set; }
    }
}
