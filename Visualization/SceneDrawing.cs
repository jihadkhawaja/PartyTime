using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using BitmapSource = Avalonia.Media.Imaging.Bitmap;
using PartyTime.Settings;

namespace PartyTime.Visualization;

internal interface ISceneDrawing
{
    void SetImages(ReadOnlySpan<BitmapSource> images);
    void DrawImage(BitmapSource image, Rect bounds, double opacity);
    void DrawRectangle(Brush brush, Rect bounds);
    void DrawRoundedRectangle(Brush? brush, Pen? pen, Rect bounds, double radius);
    void DrawEllipse(Brush? brush, Pen? pen, Point center, double radius);
    void DrawLine(Pen pen, Point start, Point end);
    void DrawPolygon(ReadOnlySpan<Point> points, Brush? brush, Pen? pen);
    void DrawSymbol(ShapeKind shape, Point center, double radius, double rotation, Color color, double opacity, bool filled);
    void DrawGlow(Point center, double radius, Color color, double opacity);
    void DrawColoredLine(Point start, Point end, Color color, double opacity, double thickness);
    void DrawCurve(Point start, ReadOnlySpan<(Point Control, Point End)> segments, Pen halo, Pen stroke);
}

internal sealed class AvaloniaSceneDrawing(DrawingContext drawing) : ISceneDrawing
{
    public void SetImages(ReadOnlySpan<BitmapSource> images) { }
    public void DrawImage(BitmapSource image, Rect bounds, double opacity)
    {
        using var opacityState = drawing.PushOpacity(opacity);
        drawing.DrawImage(image, bounds);
    }
    public void DrawRectangle(Brush brush, Rect bounds) => drawing.DrawRectangle(brush, null, bounds);

    public void DrawRoundedRectangle(Brush? brush, Pen? pen, Rect bounds, double radius)
        => drawing.DrawRectangle(brush, pen, bounds, radius, radius);

    public void DrawEllipse(Brush? brush, Pen? pen, Point center, double radius)
        => drawing.DrawEllipse(brush, pen, center, radius, radius);

    public void DrawLine(Pen pen, Point start, Point end) => drawing.DrawLine(pen, start, end);

    private static Color WithOpacity(Color color, double opacity) => Color.FromArgb((byte)Math.Clamp(Math.Round(255 * opacity), 0, 255), color.R, color.G, color.B);

    public void DrawColoredLine(Point start, Point end, Color color, double opacity, double thickness)
        => drawing.DrawLine(new Pen(new SolidColorBrush(WithOpacity(color, opacity)), thickness)
            { LineCap = PenLineCap.Round }, start, end);

    public void DrawGlow(Point center, double radius, Color color, double opacity)
    {
        drawing.DrawEllipse(new RadialGradientBrush { GradientStops = [new(WithOpacity(color, opacity * 0.45), 0), new(WithOpacity(color, 0), 1)] }, null, center, radius * 1.7, radius * 1.7);
        var core = Color.FromRgb((byte)(color.R * 0.7 + 255 * 0.3), (byte)(color.G * 0.7 + 255 * 0.3), (byte)(color.B * 0.7 + 255 * 0.3));
        drawing.DrawEllipse(new SolidColorBrush(WithOpacity(core, opacity)), null, center, radius * 0.28, radius * 0.28);
    }

    public void DrawSymbol(ShapeKind shape, Point center, double radius, double rotation, Color color, double opacity, bool filled)
    {
        var (sin, cos) = Math.SinCos(rotation);
        var transform = new MatrixTransform(new Matrix(radius * cos, radius * sin, -radius * sin, radius * cos, center.X, center.Y));
        var stroke = new SolidColorBrush(WithOpacity(color, opacity));
        var fill = filled ? new SolidColorBrush(WithOpacity(color, opacity * 0.78)) : null;
        using var transformState = drawing.PushTransform(transform.Value);
        drawing.DrawGeometry(fill, new Pen(stroke, Math.Clamp(radius * 0.1, 0.7, 3) / radius), SymbolGeometry.Get(shape));
    }

    public void DrawPolygon(ReadOnlySpan<Point> points, Brush? brush, Pen? pen)
    {
        if (points.Length < 3)
            return;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[0], brush != null);
            for (int i = 1; i < points.Length; i++)
                path.LineTo(points[i]);
            path.EndFigure(true);
        }
        drawing.DrawGeometry(brush, pen, geometry);
    }

    public void DrawCurve(Point start, ReadOnlySpan<(Point Control, Point End)> segments, Pen halo, Pen stroke)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(start, false);
            foreach (var segment in segments)
                path.QuadraticBezierTo(segment.Control, segment.End);
            path.EndFigure(false);
        }
        drawing.DrawGeometry(null, halo, geometry);
        drawing.DrawGeometry(null, stroke, geometry);
    }
}
