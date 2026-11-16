using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PartyTime.Controls;

public sealed class Checkerboard : Control
{
    private static readonly IBrush Light = new SolidColorBrush(Color.Parse("#41394C"));
    private static readonly IBrush Dark = new SolidColorBrush(Color.Parse("#2B2534"));
    public override void Render(DrawingContext context)
    {
        using var clip = context.PushClip(new Rect(Bounds.Size));
        for (int y = 0; y < Bounds.Height; y += 12)
            for (int x = 0; x < Bounds.Width; x += 12)
                context.DrawRectangle(((x + y) / 12 % 2 == 0) ? Light : Dark, null, new Rect(x, y, 12, 12));
    }
}
