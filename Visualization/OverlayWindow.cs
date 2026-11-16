using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PartyTime.Interop;
using PartyTime.Settings;

namespace PartyTime.Visualization;

internal sealed class OverlayWindow : Window
{
    public OverlaySurface Surface { get; } = new();
    public OverlayWindow()
    {
        Title = "PartyTime Overlay";
        Width = Height = 100;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        CanResize = false;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Content = Surface;
        Closed += (_, _) => Surface.Dispose();
    }

    public void Open(Screen screen)
    {
        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / screen.Scaling;
        Height = screen.Bounds.Height / screen.Scaling;
        Surface.DesktopOrigin = new Point(screen.Bounds.X, screen.Bounds.Y);
        Surface.Scaling = screen.Scaling;
        Show();
        try { Desktop.MakeClickThrough(this); }
        catch { Close(); throw; }
    }
    public bool SetCaptureExcluded(bool exclude) => Desktop.SetCaptureExcluded(this, exclude);
}

internal sealed class OverlaySurface : Panel, IDisposable
{
    private readonly SceneLayer _base = new(false);
    private readonly SceneLayer _cursor = new(true);
    private readonly SampleLayer _sample = new();
    private readonly ScreenSampleLayer _screen = new();
    private bool _baseVisible, _cursorVisible;
    public Point DesktopOrigin { get; set; }
    public double Scaling { get; set; } = 1;
    public OverlaySurface()
    {
        ClipToBounds = true;
        Children.Add(_sample);
        Children.Add(_screen);
        Children.Add(_base);
        Children.Add(_cursor);
    }
    public void ShowScene(Scene? scene, bool redrawBase = true, bool drawCursor = true, Bitmap? sample = null, double sampleOpacity = 0)
    {
        if (redrawBase)
        {
            bool visible = scene?.BaseVisible == true;
            if (visible || _baseVisible) { _base.Scene = scene; _base.InvalidateVisual(); }
            _baseVisible = visible;
            if (_sample.Sample != sample || _sample.SampleOpacity != sampleOpacity)
            {
                _sample.Sample = sample;
                _sample.SampleOpacity = sampleOpacity;
                _sample.InvalidateVisual();
            }
        }
        bool cursorVisible = drawCursor && scene?.HasCursorTrail == true;
        if (cursorVisible || _cursorVisible)
        {
            _cursor.Scene = cursorVisible ? scene : null;
            _cursor.DesktopOrigin = DesktopOrigin;
            _cursor.Scaling = Scaling;
            _cursor.InvalidateVisual();
        }
        _cursorVisible = cursorVisible;
    }
    public void ShowScreenSample(ScreenSampleFrame? frame, ScreenSampleRequest request, long now)
        => _screen.ShowFrame(frame, request.Settings.SampleBlend,
            1 + request.Settings.SampleZoom / 100.0 * request.Pulse(now), frame == null ? 0 : request.Opacity(now));

    public void ClearScreenSample() => _screen.ShowFrame(null, ScreenSampleBlend.Normal, 1, 0);
    public void Dispose() { _screen.Dispose(); _base.Dispose(); _cursor.Dispose(); }
    private sealed class SampleLayer : Control
    {
        public Bitmap? Sample;
        public double SampleOpacity;
        public override void Render(DrawingContext context)
        {
            if (Sample == null || SampleOpacity < 0.005) return;
            using var state = context.PushOpacity(SampleOpacity);
            context.DrawImage(Sample, new Rect(Bounds.Size));
        }
    }
    private sealed class SceneLayer(bool cursor) : Control, IDisposable
    {
        private SkiaSceneRenderer? _renderer;
        public Scene? Scene;
        public Point DesktopOrigin;
        public double Scaling = 1;
        public override void Render(DrawingContext context)
        {
            if (Scene == null || !cursor && !Scene.BaseVisible)
            {
                _renderer?.SetImages([]);
                return;
            }
            _renderer ??= new SkiaSceneRenderer();
            var picture = _renderer.Record(Scene, Bounds.Width, Bounds.Height, cursor, DesktopOrigin, Scaling);
            context.Custom(new ScenePictureOperation(new Rect(Bounds.Size), picture));
        }
        public void Dispose() { _renderer?.Dispose(); _renderer = null; }
    }
}
