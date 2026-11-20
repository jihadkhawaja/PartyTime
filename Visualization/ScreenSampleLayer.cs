using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PartyTime.Visualization;

internal sealed class ScreenSampleLayer : Control, IDisposable
{
    private ScreenSampleFrame? _frame;
    private double _zoom = 1;
    public ScreenSampleLayer()
    {
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.MediumQuality);
    }

    // UI thread owns this lease. Recorded DrawImage operations retain native bitmap references.
    public void ShowFrame(ScreenSampleFrame? frame, double opacity, double zoom = 1)
    {
        bool changed = _frame?.Bitmap != frame?.Bitmap || _zoom != zoom;
        var previous = _frame;
        _frame = frame;
        _zoom = zoom;
        Opacity = opacity;
        if (changed) InvalidateVisual();
        previous?.Dispose();
    }

    public override void Render(DrawingContext context)
    {
        if (_frame == null) return;

        var bitmap = _frame.Bitmap;
        var source = new Rect(bitmap.Size);
        // Only Zoom Only uses a continuous texture crop. The original effects arrive preblended.
        if (_zoom != 1)
        {
            double width = source.Width / _zoom, height = source.Height / _zoom;
            source = new Rect((source.Width - width) / 2, (source.Height - height) / 2, width, height);
        }
        context.DrawImage(bitmap, source, new Rect(Bounds.Size));
    }

    public void Dispose() { var previous = _frame; _frame = null; previous?.Dispose(); }
}
