using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PartyTime.Settings;

namespace PartyTime.Visualization;

internal sealed class ScreenSampleLayer : Control, IDisposable
{
    private ScreenSampleFrame? _frame;
    private ScreenSampleBlend _blend;
    private double _zoom = 1;
    public ScreenSampleLayer()
    {
        ClipToBounds = true;
        RenderOptions.SetRequiresFullOpacityHandling(this, true);
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    // UI thread owns this lease. Recorded DrawImage operations retain native bitmap references.
    public void ShowFrame(ScreenSampleFrame? frame, ScreenSampleBlend blend, double zoom, double opacity)
    {
        bool changed = _frame?.Held != frame?.Held || _frame?.Current != frame?.Current || _blend != blend || _zoom != zoom;
        var previous = _frame;
        _frame = frame; _blend = blend; _zoom = zoom;
        Opacity = opacity;
        if (changed) InvalidateVisual();
        previous?.Dispose();
    }

    public override void Render(DrawingContext context)
    {
        if (_frame == null) return;
        Draw(context, new Rect(Bounds.Size), _frame, _blend, _zoom);
    }

    internal static void Draw(DrawingContext context, Rect bounds, ScreenSampleFrame frame, ScreenSampleBlend blend, double zoom)
    {
        bool composite = NeedsBackground(blend);
        if (composite && frame.Current != null) context.DrawImage(frame.Current, bounds);
        var source = new Rect(frame.Held.Size);
        // Cropping the texture instead of resampling pixels keeps zoom smooth at display cadence.
        double width = source.Width / zoom, height = source.Height / zoom;
        source = new Rect((source.Width - width) / 2, (source.Height - height) / 2, width, height);
        using var options = context.PushRenderOptions(new RenderOptions { BitmapBlendingMode = blend switch
        {
            ScreenSampleBlend.Additive => BitmapBlendingMode.Plus,
            ScreenSampleBlend.Screen => BitmapBlendingMode.Screen,
            ScreenSampleBlend.Multiply => BitmapBlendingMode.Multiply,
            ScreenSampleBlend.Difference => BitmapBlendingMode.Difference,
            _ => BitmapBlendingMode.SourceOver
        } });
        context.DrawImage(frame.Held, source, bounds);
    }

    public static bool NeedsBackground(ScreenSampleBlend blend)
        => blend is ScreenSampleBlend.Additive or ScreenSampleBlend.Screen or ScreenSampleBlend.Multiply or ScreenSampleBlend.Difference;

    public void Dispose() { var previous = _frame; _frame = null; previous?.Dispose(); }
}
