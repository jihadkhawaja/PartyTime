using System;
using System.Windows;
using System.Windows.Media;

namespace PartyTime.Visualization;

internal interface ISceneDrawing
{
    void DrawRectangle(Brush brush, Rect bounds);
    void DrawRoundedRectangle(Brush? brush, Pen? pen, Rect bounds, double radius);
    void DrawEllipse(Brush? brush, Pen? pen, Point center, double radius);
    void DrawLine(Pen pen, Point start, Point end);
    void DrawCurve(Point start, ReadOnlySpan<(Point Control, Point End)> segments, Pen halo, Pen stroke);
}

internal sealed class WpfSceneDrawing(DrawingContext drawing) : ISceneDrawing
{
    public void DrawRectangle(Brush brush, Rect bounds) => drawing.DrawRectangle(brush, null, bounds);

    public void DrawRoundedRectangle(Brush? brush, Pen? pen, Rect bounds, double radius)
        => drawing.DrawRoundedRectangle(brush, pen, bounds, radius, radius);

    public void DrawEllipse(Brush? brush, Pen? pen, Point center, double radius)
        => drawing.DrawEllipse(brush, pen, center, radius, radius);

    public void DrawLine(Pen pen, Point start, Point end) => drawing.DrawLine(pen, start, end);

    public void DrawCurve(Point start, ReadOnlySpan<(Point Control, Point End)> segments, Pen halo, Pen stroke)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(start, false, false);
            foreach (var segment in segments)
                path.QuadraticBezierTo(segment.Control, segment.End, true, true);
        }
        geometry.Freeze();
        drawing.DrawGeometry(null, halo, geometry);
        drawing.DrawGeometry(null, stroke, geometry);
    }
}
