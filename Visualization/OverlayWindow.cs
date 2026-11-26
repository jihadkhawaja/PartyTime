using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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
    private readonly ScreenSampleLayer _screen = new();
    private bool _baseVisible, _cursorVisible;
    public Point DesktopOrigin { get; set; }
    public double Scaling { get; set; } = 1;
    public OverlaySurface()
    {
        ClipToBounds = true;
        Children.Add(_screen);
        Children.Add(_base);
        Children.Add(_cursor);
    }
    public void ShowScene(Scene? scene, bool redrawBase = true, bool drawCursor = true, Color[]? ambientColors = null)
    {
        if (redrawBase)
        {
            bool visible = scene?.BaseVisible == true;
            _base.AmbientColors = ambientColors;
            if (visible || _baseVisible) { _base.Scene = scene; _base.InvalidateVisual(); }
            _baseVisible = visible;
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
    public void ShowScreenSample(ScreenSampleFrame? frame, double opacity, double zoom = 1)
        => _screen.ShowFrame(frame, frame == null ? 0 : opacity, zoom);

    public void ClearScreenSample() => _screen.ShowFrame(null, 0);
    public void Dispose() { _screen.Dispose(); _base.Dispose(); _cursor.Dispose(); }
    private sealed class SceneLayer(bool cursor) : Control, IDisposable
    {
        private SkiaSceneRenderer? _renderer;
        public Scene? Scene;
        public Color[]? AmbientColors;
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
            var picture = _renderer.Record(Scene, Bounds.Width, Bounds.Height, cursor, DesktopOrigin, Scaling, ambientColors: AmbientColors);
            context.Custom(new ScenePictureOperation(new Rect(Bounds.Size), picture));
        }
        public void Dispose() { _renderer?.Dispose(); _renderer = null; }
    }
}
