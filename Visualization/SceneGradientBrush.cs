using Avalonia;
using Avalonia.Media;

namespace PartyTime.Visualization;

internal static class SceneBrush
{
    public static LinearGradientBrush Linear(Color first, Color second, Point start, Point end) => new()
    {
        StartPoint = new RelativePoint(start, RelativeUnit.Relative),
        EndPoint = new RelativePoint(end, RelativeUnit.Relative),
        GradientStops = [new(first, 0), new(second, 1)]
    };
}
