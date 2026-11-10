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
        => Desktop.SetCaptureExcluded(new WindowInteropHelper(this).Handle, exclude);
}

internal sealed class OverlaySurface : FrameworkElement
{
    private Scene? _scene;

    public void ShowScene(Scene? scene)
    {
        if (_scene is null && scene is null)
            return;
        _scene = scene;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (_scene is { } scene && ActualWidth > 0 && ActualHeight > 0)
            scene.Draw(drawing, ActualWidth, ActualHeight);
    }
}
