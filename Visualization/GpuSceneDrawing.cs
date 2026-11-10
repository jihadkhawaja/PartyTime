using System;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Color = System.Windows.Media.Color;
using Rect = System.Windows.Rect;
using Vector = System.Windows.Vector;

namespace PartyTime.Visualization;

internal sealed class GpuSceneDrawing(ID2D1Factory factory, ID2D1DeviceContext context) : ISceneDrawing, IDisposable
{
    private readonly ID2D1StrokeStyle _roundCaps = factory.CreateStrokeStyle(new StrokeStyleProperties
    {
        StartCap = CapStyle.Round,
        EndCap = CapStyle.Round
    });
    private Brush? _source;
    private ID2D1Brush? _brush;

    public void DrawRectangle(Brush brush, Rect bounds)
        => context.FillRectangle(ToNative(bounds), GetBrush(brush, bounds));

    public void DrawRoundedRectangle(Brush? brush, Pen? pen, Rect bounds, double radius)
    {
        var rectangle = new RoundedRectangle(ToNative(bounds), (float)radius, (float)radius);
        if (brush != null)
            context.FillRoundedRectangle(rectangle, GetBrush(brush, bounds));
        if (pen != null)
            context.DrawRoundedRectangle(rectangle, GetBrush(pen.Brush, bounds), (float)pen.Thickness);
    }

    public void DrawEllipse(Brush? brush, Pen? pen, Point center, double radius)
    {
        var ellipse = new Ellipse(ToNative(center), (float)radius, (float)radius);
        var bounds = new Rect(center.X - radius, center.Y - radius, 2 * radius, 2 * radius);
        if (brush != null)
            context.FillEllipse(ellipse, GetBrush(brush, bounds));
        if (pen != null)
            context.DrawEllipse(ellipse, GetBrush(pen.Brush, bounds), (float)pen.Thickness);
    }

    public void DrawLine(Pen pen, Point start, Point end)
        => context.DrawLine(ToNative(start), ToNative(end), GetBrush(pen.Brush, Rect.Empty), (float)pen.Thickness, _roundCaps);

    public void DrawCurve(Point start, ReadOnlySpan<(Point Control, Point End)> segments, Pen halo, Pen stroke)
    {
        using var geometry = factory.CreatePathGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(ToNative(start), FigureBegin.Hollow);
            foreach (var segment in segments)
                sink.AddQuadraticBezier(new Vortice.Direct2D1.QuadraticBezierSegment
                {
                    Point1 = ToNative(segment.Control),
                    Point2 = ToNative(segment.End)
                });
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        DrawCurveStroke(geometry, halo);
        DrawCurveStroke(geometry, stroke);
    }

    private void DrawCurveStroke(ID2D1Geometry geometry, Pen pen)
    {
        var native = geometry.GetWidenedBounds((float)pen.Thickness);
        var bounds = new Rect(native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top);
        context.DrawGeometry(geometry, GetBrush(pen.Brush, bounds), (float)pen.Thickness);
    }

    private ID2D1Brush GetBrush(Brush source, Rect bounds)
    {
        if (!ReferenceEquals(source, _source))
        {
            ClearBrush();
            _brush = source switch
            {
                SolidColorBrush solid => context.CreateSolidColorBrush(ToNative(solid.Color)),
                LinearGradientBrush linear => CreateLinearBrush(linear),
                RadialGradientBrush radial => CreateRadialBrush(radial),
                _ => throw new InvalidOperationException("Unsupported overlay brush.")
            };
            _source = source;
        }

        if (source is GradientBrush)
            _brush!.Transform = new Matrix3x2((float)Math.Max(0.001, bounds.Width), 0, 0,
                (float)Math.Max(0.001, bounds.Height), (float)bounds.X, (float)bounds.Y);

        return _brush!;
    }

    private ID2D1LinearGradientBrush CreateLinearBrush(LinearGradientBrush source)
    {
        using var stops = CreateStops(source);
        return context.CreateLinearGradientBrush(new LinearGradientBrushProperties(
            ToNative(source.StartPoint), ToNative(source.EndPoint)), stops);
    }

    private ID2D1RadialGradientBrush CreateRadialBrush(RadialGradientBrush source)
    {
        using var stops = CreateStops(source);
        return context.CreateRadialGradientBrush(new RadialGradientBrushProperties(ToNative(source.Center),
            ToNative(source.GradientOrigin - source.Center), (float)source.RadiusX, (float)source.RadiusY), stops);
    }

    private ID2D1GradientStopCollection CreateStops(GradientBrush source)
    {
        var stops = new Vortice.Direct2D1.GradientStop[source.GradientStops.Count];
        for (int i = 0; i < stops.Length; i++)
        {
            var stop = source.GradientStops[i];
            stops[i] = new Vortice.Direct2D1.GradientStop((float)stop.Offset, ToNative(stop.Color));
        }
        return context.CreateGradientStopCollection(stops, Gamma.StandardRgb, ExtendMode.Clamp);
    }

    private static Vector2 ToNative(Point point) => new((float)point.X, (float)point.Y);
    private static Vector2 ToNative(Vector vector) => new((float)vector.X, (float)vector.Y);
    private static RawRectF ToNative(Rect bounds) => new((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom);
    private static Color4 ToNative(Color color) => new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);

    private void ClearBrush()
    {
        _brush?.Dispose();
        _brush = null;
        _source = null;
    }

    public void Dispose()
    {
        ClearBrush();
        _roundCaps.Dispose();
    }
}
