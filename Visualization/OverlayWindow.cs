using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PartyTime.Interop;
using Rectangle = System.Drawing.Rectangle;

namespace PartyTime.Visualization;

internal sealed class OverlayWindow : Window
{
    public OverlaySurface Surface { get; } = new();
    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public OverlayWindow()
    {
        Title = "PartyTime Overlay";
        Width = 100;
        Height = 100;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = false;
        Content = Surface;
    }

    public void Open(Rectangle bounds)
    {
        IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
        Desktop.MakeClickThrough(handle);
        Show();
        Desktop.PlaceOverlay(handle, bounds);
    }

    public bool SetCaptureExcluded(bool exclude)
        => Desktop.SetCaptureExcluded(Handle, exclude);
}

internal sealed class OverlaySurface : FrameworkElement
{
    private readonly DrawingVisual _drawing = new();
    private Scene? _scene;

    public OverlaySurface() => AddVisualChild(_drawing);

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index)
        => index == 0 ? _drawing : throw new ArgumentOutOfRangeException(nameof(index));

    public void ShowScene(Scene? scene)
    {
        if (_scene is null && scene is null)
            return;
        _scene = scene;
        Draw();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_scene != null)
            Draw();
    }

    private void Draw()
    {
        using var drawing = _drawing.RenderOpen();
        if (_scene is { } scene && ActualWidth > 0 && ActualHeight > 0)
            scene.Draw(drawing, ActualWidth, ActualHeight);
    }
}
