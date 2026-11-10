using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Media;
using PartyTime.Audio;
using PartyTime.Settings;
using Screen = System.Windows.Forms.Screen;

namespace PartyTime.Visualization;

internal sealed class Overlay : IDisposable
{
    private readonly SystemAudio _audio;
    private OverlayWindow[] _windows = [];
    private readonly Scene _scene = new();
    private ScreenColorSampler? _sampler;
    private TimeSpan? _lastRender;
    private bool _useScreenColors;

    public Overlay(SystemAudio audio)
    {
        _audio = audio;
        RebuildScreens();
        CompositionTarget.Rendering += Render;
    }

    public Color[]? ScreenColors => _sampler?.Latest;
    public bool CanSampleScreen => _sampler != null;

    public void ApplySettings(VisualizerSettings settings)
    {
        _scene.ApplySettings(settings);
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
        foreach (var window in _windows)
            canSample &= window.SetCaptureExcluded(_useScreenColors);

        if (canSample)
        {
            _sampler = new ScreenColorSampler();
        }
        else if (_useScreenColors)
        {
            foreach (var window in _windows)
                window.SetCaptureExcluded(false);
        }
    }

    public void RebuildScreens()
    {
        foreach (var window in _windows)
            window.Close();

        var screens = Screen.AllScreens;
        var windows = new List<OverlayWindow>(screens.Length);
        try
        {
            foreach (var screen in screens)
            {
                var window = new OverlayWindow();
                windows.Add(window);
                window.Open(screen.Bounds);
            }
            _windows = windows.ToArray();
            if (_useScreenColors)
                ConfigureSampler();
        }
        catch
        {
            foreach (var window in windows)
                window.Close();
            _windows = [];
            throw;
        }
    }

    public void Dispose()
    {
        CompositionTarget.Rendering -= Render;
        _sampler?.Dispose();
        foreach (var window in _windows)
            window.Close();
        _windows = [];
    }

    private void Render(object? sender, EventArgs args)
    {
        var time = ((RenderingEventArgs)args).RenderingTime;
        if (_lastRender is { } previous && time - previous < TimeSpan.FromMilliseconds(32))
            return;

        double elapsed = _lastRender is { } last
            ? Math.Clamp((time - last).TotalSeconds, 0.01, 0.2)
            : 1.0 / 30;
        _lastRender = time;

        bool visible = _scene.Update(_audio.Latest, Stopwatch.GetTimestamp(), elapsed, ScreenColors);
        foreach (var window in _windows)
            window.Surface.ShowScene(visible ? _scene : null);
    }
}
