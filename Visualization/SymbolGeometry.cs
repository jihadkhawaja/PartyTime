using System;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using PartyTime.Settings;

namespace PartyTime.Visualization;

internal static class SymbolGeometry
{
    private static readonly Point[][] Vertices = Enumerable.Range(0, (int)ShapeKind.Mixed).Select(index => Build((ShapeKind)index)).ToArray();
    private static readonly Geometry[] Geometries = Enumerable.Range(0, (int)ShapeKind.Mixed).Select(index => CreateGeometry((ShapeKind)index)).ToArray();

    public static ReadOnlySpan<Point> Points(ShapeKind shape) => Vertices[(int)shape];
    public static Geometry Get(ShapeKind shape) => Geometries[(int)shape];

    private static Point[] Build(ShapeKind shape)
    {
        int count = shape switch
        {
            ShapeKind.Circle => 0, ShapeKind.Triangle => 3, ShapeKind.Star => 10,
            ShapeKind.Hexagon => 6, ShapeKind.Heart => 32, ShapeKind.Cross => 12,
            ShapeKind.Pentagon => 5, ShapeKind.Octagon => 8, _ => 4
        };
        ReadOnlySpan<Point> cross = [new(-0.3, -1), new(0.3, -1), new(0.3, -0.3), new(1, -0.3),
            new(1, 0.3), new(0.3, 0.3), new(0.3, 1), new(-0.3, 1), new(-0.3, 0.3), new(-1, 0.3), new(-1, -0.3), new(-0.3, -0.3)];
        var points = new Point[count];
        for (int i = 0; i < count; i++)
        {
            double angle = Math.Tau * i / count - Math.PI / 2 + (shape == ShapeKind.Square ? Math.PI / 4 : 0);
            double length = shape == ShapeKind.Star && i % 2 != 0 ? 0.43 : 1;
            double x = Math.Cos(angle) * length, y = Math.Sin(angle) * length;
            if (shape == ShapeKind.Cross)
                (x, y) = (cross[i].X, cross[i].Y);
            if (shape == ShapeKind.Heart)
            {
                double t = Math.Tau * i / count;
                x = Math.Pow(Math.Sin(t), 3);
                y = -(13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t)) / 17;
            }
            points[i] = new(x, y);
        }
        return points;
    }

    private static Geometry CreateGeometry(ShapeKind shape)
    {
        if (shape == ShapeKind.Circle)
        {
            var circle = new EllipseGeometry(new Rect(-1, -1, 2, 2));
            return circle;
        }
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            var points = Points(shape);
            path.BeginFigure(points[0], true);
            for (int i = 1; i < points.Length; i++)
                path.LineTo(points[i]);
            path.EndFigure(true);
        }
        return geometry;
    }
}
