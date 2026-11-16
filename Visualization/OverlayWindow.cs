using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        Surface.DesktopOrigin = new Point(bounds.X, bounds.Y);
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
    private readonly DrawingVisual _baseDrawing = new();
    private readonly DrawingVisual _cursorDrawing = new();
    private readonly DrawingVisual _sampleDrawing = new();
    private BitmapSource? _sample;
    private double _sampleOpacity;
    private Scene? _scene;
    private bool _baseVisible;
    private bool _cursorVisible;
    public Point DesktopOrigin { get; set; }

    public OverlaySurface()
    {
        ClipToBounds = true;
        AddVisualChild(_sampleDrawing);
        AddVisualChild(_baseDrawing);
        AddVisualChild(_cursorDrawing);
    }

    protected override int VisualChildrenCount => 3;
    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _sampleDrawing, 1 => _baseDrawing, 2 => _cursorDrawing, _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public void ShowScene(Scene? scene, bool redrawBase = true, bool drawCursor = true, BitmapSource? sample = null, double sampleOpacity = 0)
    {
        _scene = scene;
        _sample = sample;
        _sampleOpacity = sampleOpacity;
        Draw(redrawBase, drawCursor);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Draw(true, _cursorVisible);
    }

    private void Draw(bool redrawBase, bool drawCursor)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;
        if (redrawBase)
        {
            using var drawing = _sampleDrawing.RenderOpen();
            if (_sample != null && _sampleOpacity > 0)
            {
                drawing.PushOpacity(_sampleOpacity);
                drawing.DrawImage(_sample, new Rect(0, 0, ActualWidth, ActualHeight));
                drawing.Pop();
            }
        }
        if (redrawBase && (_baseVisible || _scene?.BaseVisible == true))
        {
            using var drawing = _baseDrawing.RenderOpen();
            _baseVisible = _scene?.BaseVisible == true;
            if (_baseVisible)
                _scene!.DrawBase(new WpfSceneDrawing(drawing), ActualWidth, ActualHeight);
        }
        if (drawCursor || _cursorVisible)
        {
            using var drawing = _cursorDrawing.RenderOpen();
            _cursorVisible = drawCursor && _scene?.HasCursorTrail == true;
            if (_cursorVisible)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                _scene!.DrawCursor(new WpfSceneDrawing(drawing), ActualWidth, ActualHeight, DesktopOrigin, dpi.DpiScaleX, dpi.DpiScaleY);
            }
        }
    }
}
