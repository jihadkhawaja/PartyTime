using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PartyTime.Interop;

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

internal sealed class OverlaySurface : Panel
{
    private readonly SceneLayer _base = new(false);
    private readonly SceneLayer _cursor = new(true);
    private readonly SampleLayer _sample = new();
    public Point DesktopOrigin { get; set; }
    public double Scaling { get; set; } = 1;
    public OverlaySurface()
    {
        ClipToBounds = true;
        Children.Add(_sample);
        Children.Add(_base);
        Children.Add(_cursor);
    }
    public void ShowScene(Scene? scene, bool redrawBase = true, bool drawCursor = true, Bitmap? sample = null, double sampleOpacity = 0)
    {
        if (redrawBase)
        {
            _base.Scene = scene;
            _base.InvalidateVisual();
            _sample.Sample = sample;
            _sample.SampleOpacity = sampleOpacity;
            _sample.InvalidateVisual();
        }
        _cursor.Scene = drawCursor ? scene : null;
        _cursor.DesktopOrigin = DesktopOrigin;
        _cursor.Scaling = Scaling;
        _cursor.InvalidateVisual();
    }
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
    private sealed class SceneLayer(bool cursor) : Control
    {
        public Scene? Scene;
        public Point DesktopOrigin;
        public double Scaling = 1;
        public override void Render(DrawingContext context)
        {
            if (cursor) Scene?.DrawCursor(new AvaloniaSceneDrawing(context), Bounds.Width, Bounds.Height, DesktopOrigin, Scaling, Scaling);
            else if (Scene?.BaseVisible == true) Scene.DrawBase(new AvaloniaSceneDrawing(context), Bounds.Width, Bounds.Height);
        }
    }
}
