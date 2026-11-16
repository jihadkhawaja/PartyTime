using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace PartyTime.Visualization;

internal static class SceneBrush
{
    public static ImmutableLinearGradientBrush Linear(Color first, Color second, Point start, Point end)
        => Linear(start, end, new(0, first), new(1, second));

    public static ImmutableLinearGradientBrush Linear(Point start, Point end, params ImmutableGradientStop[] stops)
        => new(stops, startPoint: new RelativePoint(start, RelativeUnit.Relative), endPoint: new RelativePoint(end, RelativeUnit.Relative));

    public static ImmutableRadialGradientBrush Radial(params ImmutableGradientStop[] stops) => new(stops, radiusX: new RelativeScalar(0.5, RelativeUnit.Relative), radiusY: new RelativeScalar(0.5, RelativeUnit.Relative));
}
