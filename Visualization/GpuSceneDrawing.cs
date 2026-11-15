using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PartyTime.Settings;
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
    private readonly Dictionary<BitmapSource, ID2D1Bitmap> _images = [];
    private readonly List<BitmapSource> _unusedImages = [];
    private readonly ID2D1SolidColorBrush _solid = context.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
    private readonly Dictionary<ShapeKind, ID2D1PathGeometry> _symbols = [];
    private readonly ID2D1DeviceContext1 _realizedContext = context.QueryInterface<ID2D1DeviceContext1>();
    private readonly Dictionary<ShapeKind, ID2D1GeometryRealization> _filledSymbols = [];
    private readonly Dictionary<(ShapeKind Shape, int Width), ID2D1GeometryRealization> _strokedSymbols = [];
    private ID2D1Bitmap? _glowMask;

    private ID2D1SolidColorBrush Solid(Color color, double opacity = 1)
    {
        _solid.Color = new Color4(color.R / 255f, color.G / 255f, color.B / 255f, (float)Math.Clamp(color.A / 255.0 * opacity, 0, 1));
        return _solid;
    }

    public void DrawColoredLine(Point start, Point end, Color color, double opacity, double thickness)
        => context.DrawLine(ToNative(start), ToNative(end), Solid(color, opacity), (float)thickness, _roundCaps);

    public unsafe void DrawGlow(Point center, double radius, Color color, double opacity)
    {
        if (_glowMask == null)
        {
            const int size = 64;
            var pixels = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    double distance = Math.Sqrt(Math.Pow((x + 0.5 - size / 2) / (size / 2), 2) + Math.Pow((y + 0.5 - size / 2) / (size / 2), 2));
                    byte alpha = (byte)Math.Round(Math.Clamp(1 - distance, 0, 1) * 255);
                    int index = (y * size + x) * 4;
                    pixels[index] = pixels[index + 1] = pixels[index + 2] = pixels[index + 3] = alpha;
                }
            fixed (byte* data = pixels)
                _glowMask = context.CreateBitmap(new SizeI(size, size), (IntPtr)data, size * 4,
                    new BitmapProperties(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm,
                        Vortice.DCommon.AlphaMode.Premultiplied), 96, 96));
        }
        double outer = radius * 1.7;
        var bounds = new Rect(center.X - outer, center.Y - outer, outer * 2, outer * 2);
        var previousAntialias = context.AntialiasMode;
        context.AntialiasMode = AntialiasMode.Aliased;
        context.FillOpacityMask(_glowMask, Solid(color, opacity * 0.45), ToNative(bounds), null);
        context.AntialiasMode = previousAntialias;
        var core = Color.FromRgb((byte)(color.R * 0.7 + 255 * 0.3), (byte)(color.G * 0.7 + 255 * 0.3), (byte)(color.B * 0.7 + 255 * 0.3));
        context.FillEllipse(new Ellipse(ToNative(center), (float)(radius * 0.28), (float)(radius * 0.28)), Solid(core, opacity));
    }

    public void DrawSymbol(ShapeKind shape, Point center, double radius, double rotation, Color color, double opacity, bool filled)
    {
        float thickness = (float)Math.Clamp(radius * 0.1, 0.7, 3);
        if (shape == ShapeKind.Circle)
        {
            var circle = new Ellipse(ToNative(center), (float)radius, (float)radius);
            if (filled) context.FillEllipse(circle, Solid(color, opacity * 0.78));
            context.DrawEllipse(circle, Solid(color, opacity), thickness);
            return;
        }
        if (!_symbols.TryGetValue(shape, out var geometry))
        {
            geometry = factory.CreatePathGeometry();
            using var sink = geometry.Open();
            var points = SymbolGeometry.Points(shape);
            sink.BeginFigure(ToNative(points[0]), FigureBegin.Filled);
            for (int i = 1; i < points.Length; i++) sink.AddLine(ToNative(points[i]));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
            _symbols.Add(shape, geometry);
        }
        var previous = context.Transform;
        var (sin, cos) = Math.SinCos(rotation);
        context.Transform = new Matrix3x2((float)(radius * cos), (float)(radius * sin), (float)(-radius * sin), (float)(radius * cos), (float)center.X, (float)center.Y) * previous;
        try
        {
            if (filled)
            {
                if (!_filledSymbols.TryGetValue(shape, out var fill))
                    _filledSymbols.Add(shape, fill = _realizedContext.CreateFilledGeometryRealization(geometry, 0.001f));
                _realizedContext.DrawGeometryRealization(fill, Solid(color, opacity * 0.78));
            }
            int width = Math.Max(1, (int)Math.Round(thickness / radius * 40));
            var key = (shape, width);
            if (!_strokedSymbols.TryGetValue(key, out var stroke))
            {
                if (_strokedSymbols.Count >= 256)
                {
                    foreach (var old in _strokedSymbols.Values) old.Dispose();
                    _strokedSymbols.Clear();
                }
                _strokedSymbols.Add(key, stroke = _realizedContext.CreateStrokedGeometryRealization(geometry, 0.001f, width / 40f, null));
            }
            _realizedContext.DrawGeometryRealization(stroke, Solid(color, opacity));
        }
        finally { context.Transform = previous; }
    }

    public void SetImages(ReadOnlySpan<BitmapSource> images)
    {
        _unusedImages.Clear();
        foreach (var image in _images.Keys)
            if (!images.Contains(image))
                _unusedImages.Add(image);
        foreach (var image in _unusedImages)
        {
            _images[image].Dispose();
            _images.Remove(image);
        }
    }

    public unsafe void DrawImage(BitmapSource image, Rect bounds, double opacity)
    {
        if (!_images.TryGetValue(image, out var bitmap))
        {
            int stride = image.PixelWidth * 4;
            var pixels = new byte[stride * image.PixelHeight];
            image.CopyPixels(pixels, stride, 0);
            fixed (byte* data = pixels)
                bitmap = context.CreateBitmap(new SizeI(image.PixelWidth, image.PixelHeight), (IntPtr)data, (uint)stride,
                    new BitmapProperties(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm,
                        Vortice.DCommon.AlphaMode.Premultiplied), 96, 96));
            _images.Add(image, bitmap);
        }
        context.DrawBitmap(bitmap, ToNative(bounds), (float)opacity, InterpolationMode.Linear, null, null);
    }

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

    public void DrawPolygon(ReadOnlySpan<Point> points, Brush? brush, Pen? pen)
    {
        if (points.Length < 3)
            return;
        // Hollow figures can have empty native fill bounds while their outlines are drawable.
        // Use the vertices for brush mapping so outlined shapes never produce a negative Rect.
        var bounds = Rect.Empty;
        foreach (var point in points)
        {
            if (!float.IsFinite((float)point.X) || !float.IsFinite((float)point.Y))
                return;
            bounds.Union(point);
        }
        using var geometry = factory.CreatePathGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(ToNative(points[0]), brush != null ? FigureBegin.Filled : FigureBegin.Hollow);
            for (int i = 1; i < points.Length; i++)
                sink.AddLine(ToNative(points[i]));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        if (brush != null)
            context.FillGeometry(geometry, GetBrush(brush, bounds));
        if (pen != null)
            context.DrawGeometry(geometry, GetBrush(pen.Brush, bounds), (float)pen.Thickness);
    }

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
        if (source is SolidColorBrush solidColor)
            return Solid(solidColor.Color);
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
        _glowMask?.Dispose();
        foreach (var symbol in _filledSymbols.Values) symbol.Dispose();
        foreach (var symbol in _strokedSymbols.Values) symbol.Dispose();
        _filledSymbols.Clear();
        _strokedSymbols.Clear();
        _realizedContext.Dispose();
        foreach (var geometry in _symbols.Values) geometry.Dispose();
        _symbols.Clear();
        _solid.Dispose();
        foreach (var bitmap in _images.Values)
            bitmap.Dispose();
        _images.Clear();
        ClearBrush();
        _roundCaps.Dispose();
    }
}
