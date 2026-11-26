using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using PartyTime.Settings;
using System.Threading;

namespace PartyTime.Visualization;

// Record immutable native pictures on the UI thread. The compositor replays them;
// it never reads the Scene's mutable audio/motion state.
internal sealed class SkiaSceneRenderer : ISceneDrawing, IDisposable
{
    private readonly SKPictureRecorder _recorder = new();
    private readonly SKPaint _fill = new() { IsAntialias = true };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPath _path = new();
    private readonly Dictionary<Bitmap, SKImage> _images = new();
    private readonly List<Bitmap> _unusedImages = new();
    private readonly (SKColor[] Colors, float[] Positions)[] _gradientBuffers = new (SKColor[], float[])[5];
    private readonly SKColor[] _glowColors = new SKColor[2];
    private static readonly float[] GlowStops = [0, 1];
    private static readonly SKPath[] Symbols = CreateSymbols();
    private SKCanvas _canvas = null!;

    public SKPicture Record(Scene scene, double width, double height, bool cursorOnly = false, Point origin = default,
        double scaling = 1, bool includeCursor = false, Color[]? ambientColors = null)
    {
        _canvas = _recorder.BeginRecording(new SKRect(0, 0, (float)width, (float)height));
        if (cursorOnly) scene.DrawCursor(this, width, height, origin, scaling, scaling);
        else scene.DrawBase(this, width, height, ambientColors);
        if (includeCursor && !cursorOnly) scene.DrawCursor(this, width, height, origin, scaling, scaling);
        return _recorder.EndRecording();
    }

    public void SetImages(ReadOnlySpan<Bitmap> images)
    {
        foreach (var image in images)
        {
            if (_images.ContainsKey(image)) continue;
            _images.Add(image, SkiaImageCache.Acquire(image));
        }
        _unusedImages.Clear();
        foreach (var entry in _images)
        {
            bool found = false;
            foreach (var image in images) if (ReferenceEquals(entry.Key, image)) { found = true; break; }
            if (!found) _unusedImages.Add(entry.Key);
        }
        foreach (var image in _unusedImages) { SkiaImageCache.Release(image); _images.Remove(image); }
    }

    public void DrawImage(Bitmap image, Rect bounds, double opacity)
    {
        _fill.Shader = null; _fill.Color = SKColors.White.WithAlpha(Alpha(opacity));
        _canvas.DrawImage(_images[image], Rect(bounds), new SKSamplingOptions(SKFilterMode.Linear), _fill);
    }

    public void DrawRectangle(IBrush brush, Rect bounds)
    {
        using var shader = SetBrush(_fill, brush, bounds);
        _canvas.DrawRect(Rect(bounds), _fill);
    }
    public void DrawRoundedRectangle(IBrush? brush, IPen? pen, Rect bounds, double radius)
    {
        if (brush != null) { using var shader = SetBrush(_fill, brush, bounds); _canvas.DrawRoundRect(Rect(bounds), (float)radius, (float)radius, _fill); }
        if (pen != null) { using var shader = SetPen(pen, bounds); _canvas.DrawRoundRect(Rect(bounds), (float)radius, (float)radius, _stroke); }
    }
    public void DrawEllipse(IBrush? brush, IPen? pen, Point center, double radius)
    {
        var bounds = new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        if (brush != null) { using var shader = SetBrush(_fill, brush, bounds); _canvas.DrawCircle((float)center.X, (float)center.Y, (float)radius, _fill); }
        if (pen != null) { using var shader = SetPen(pen, bounds); _canvas.DrawCircle((float)center.X, (float)center.Y, (float)radius, _stroke); }
    }
    public void DrawLine(IPen pen, Point start, Point end)
    {
        using var shader = SetPen(pen, new Rect(start, end).Normalize());
        _canvas.DrawLine((float)start.X, (float)start.Y, (float)end.X, (float)end.Y, _stroke);
    }
    public void DrawColoredLine(Point start, Point end, Color color, double opacity, double thickness)
    {
        _stroke.Shader = null; _stroke.Color = Color(color, opacity); _stroke.StrokeWidth = (float)thickness; _stroke.StrokeCap = SKStrokeCap.Round;
        _canvas.DrawLine((float)start.X, (float)start.Y, (float)end.X, (float)end.Y, _stroke);
    }
    public void DrawGlow(Point center, double radius, Color color, double opacity)
    {
        _glowColors[0] = Color(color, opacity * 0.45);
        _glowColors[1] = Color(color, 0);
        using var shader = SKShader.CreateRadialGradient(new SKPoint((float)center.X, (float)center.Y), (float)(radius * 1.7),
            _glowColors, GlowStops, SKShaderTileMode.Clamp);
        _fill.Shader = shader; _fill.Color = SKColors.White;
        _canvas.DrawCircle((float)center.X, (float)center.Y, (float)(radius * 1.7), _fill);
        _fill.Shader = null;
        _fill.Color = new SKColor((byte)(color.R * 0.7 + 255 * 0.3), (byte)(color.G * 0.7 + 255 * 0.3), (byte)(color.B * 0.7 + 255 * 0.3), Alpha(opacity));
        _canvas.DrawCircle((float)center.X, (float)center.Y, (float)(radius * 0.28), _fill);
    }
    public void DrawSymbol(ShapeKind shape, Point center, double radius, double rotation, Color color, double opacity, bool filled)
    {
        _fill.Shader = _stroke.Shader = null;
        _fill.Color = Color(color, opacity * 0.78); _stroke.Color = Color(color, opacity);
        _stroke.StrokeWidth = (float)(Math.Clamp(radius * 0.1, 0.7, 3) / radius);
        _stroke.StrokeCap = SKStrokeCap.Butt; _stroke.StrokeJoin = SKStrokeJoin.Miter;
        _stroke.StrokeMiter = 10;
        _canvas.Save();
        _canvas.Translate((float)center.X, (float)center.Y);
        _canvas.RotateRadians((float)rotation);
        _canvas.Scale((float)radius);
        if (filled) _canvas.DrawPath(Symbols[(int)shape], _fill);
        _canvas.DrawPath(Symbols[(int)shape], _stroke);
        _canvas.Restore();
    }
    public void DrawPolygon(ReadOnlySpan<Point> points, IBrush? brush, IPen? pen)
    {
        if (points.Length < 3) return;
        SetPolygon(points);
        var bounds = GetPathBrushBounds(brush, pen?.Brush);
        if (brush != null)
        {
            using var shader = SetBrush(_fill, brush, bounds);
            _canvas.DrawPath(_path, _fill);
        }
        if (pen != null) DrawPathStroke(pen, bounds);
    }
    public void DrawOutlinedPolygon(ReadOnlySpan<Point> points, Color halo, double haloThickness, Color stroke, double thickness)
    {
        if (points.Length < 3) return;
        SetPolygon(points);
        _stroke.Shader = null;
        SetDefaultStroke(haloThickness);
        _stroke.Color = ToSkiaColor(halo);
        _canvas.DrawPath(_path, _stroke);
        _stroke.StrokeWidth = (float)thickness;
        _stroke.Color = ToSkiaColor(stroke);
        _canvas.DrawPath(_path, _stroke);
    }
    private void SetPolygon(ReadOnlySpan<Point> points)
    {
        Span<SKPoint> vertices = points.Length <= 32 ? stackalloc SKPoint[points.Length] : new SKPoint[points.Length];
        for (int i = 0; i < points.Length; i++)
            vertices[i] = new SKPoint((float)points[i].X, (float)points[i].Y);
        _path.Rewind();
        _path.AddPoly(vertices, close: true);
    }
    public void DrawCurve(Point start, ReadOnlySpan<(Point Control, Point End)> segments, IPen halo, IPen stroke)
    {
        SetCurve(start, segments);
        var bounds = GetPathBrushBounds(halo.Brush, stroke.Brush);
        DrawPathStroke(halo, bounds);
        DrawPathStroke(stroke, bounds);
    }

    public void DrawHorizontalGradientCurve(Point start, ReadOnlySpan<(Point Control, Point End)> segments,
        Color first, Color second, double thickness, Color halo, double haloThickness)
    {
        SetCurve(start, segments);
        _stroke.Shader = null;
        SetDefaultStroke(haloThickness);
        _stroke.Color = ToSkiaColor(halo);
        _canvas.DrawPath(_path, _stroke);

        var bounds = _path.TightBounds;
        var (colors, positions) = GradientBuffers(2);
        colors[0] = ToSkiaColor(first);
        colors[1] = ToSkiaColor(second);
        positions[0] = 0;
        positions[1] = 1;
        float endX = (float)((double)bounds.Left + bounds.Width);
        using var shader = SKShader.CreateLinearGradient(new SKPoint(bounds.Left, bounds.Top),
            new SKPoint(endX, bounds.Top), colors, positions, SKShaderTileMode.Clamp);
        _stroke.StrokeWidth = (float)thickness;
        _stroke.Color = SKColors.White;
        _stroke.Shader = shader;
        _canvas.DrawPath(_path, _stroke);
    }

    private void SetCurve(Point start, ReadOnlySpan<(Point Control, Point End)> segments)
    {
        _path.Rewind();
        _path.MoveTo((float)start.X, (float)start.Y);
        foreach (var segment in segments)
            _path.QuadTo((float)segment.Control.X, (float)segment.Control.Y, (float)segment.End.X, (float)segment.End.Y);
    }

    private void SetDefaultStroke(double thickness)
    {
        _stroke.StrokeCap = SKStrokeCap.Butt;
        _stroke.StrokeJoin = SKStrokeJoin.Miter;
        _stroke.StrokeMiter = 10;
        _stroke.StrokeWidth = (float)thickness;
    }

    private Rect GetPathBrushBounds(IBrush? first, IBrush? second)
    {
        if (first is not IGradientBrush && second is not IGradientBrush)
            return default;
        var r = _path.TightBounds;
        return new Rect(r.Left, r.Top, r.Width, r.Height);
    }
    private void DrawPathStroke(IPen pen, Rect bounds)
    {
        using var shader = SetPen(pen, bounds);
        _canvas.DrawPath(_path, _stroke);
    }
    private SKShader? SetPen(IPen pen, Rect bounds)
    {
        _stroke.StrokeWidth = (float)pen.Thickness;
        _stroke.StrokeCap = pen.LineCap switch { PenLineCap.Round => SKStrokeCap.Round, PenLineCap.Square => SKStrokeCap.Square, _ => SKStrokeCap.Butt };
        _stroke.StrokeJoin = pen.LineJoin switch { PenLineJoin.Round => SKStrokeJoin.Round, PenLineJoin.Bevel => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter };
        _stroke.StrokeMiter = (float)pen.MiterLimit;
        return SetBrush(_stroke, pen.Brush!, bounds);
    }
    private SKShader? SetBrush(SKPaint paint, IBrush brush, Rect bounds)
    {
        if (brush is ISolidColorBrush solid)
        {
            paint.Shader = null;
            paint.Color = Color(solid.Color, solid.Opacity * solid.Color.A / 255.0);
            return null;
        }
        var gradient = (IGradientBrush)brush;
        var stops = gradient.GradientStops;
        var (colors, positions) = GradientBuffers(stops.Count);
        for (int i = 0; i < colors.Length; i++)
        {
            var stop = stops[i];
            colors[i] = ToSkiaColor(stop.Color);
            positions[i] = (float)stop.Offset;
        }
        SKPoint Point(RelativePoint relative) { var p = relative.ToPixels(bounds.Size); return new SKPoint((float)(bounds.X + p.X), (float)(bounds.Y + p.Y)); }
        SKShader shader;
        if (brush is ILinearGradientBrush linear) shader = SKShader.CreateLinearGradient(Point(linear.StartPoint), Point(linear.EndPoint), colors, positions, SKShaderTileMode.Clamp);
        else
        {
            var radial = (IRadialGradientBrush)brush;
            shader = SKShader.CreateRadialGradient(Point(radial.Center), (float)(Math.Min(bounds.Width, bounds.Height) * 0.5), colors, positions, SKShaderTileMode.Clamp);
        }
        paint.Color = SKColors.White.WithAlpha(Alpha(brush.Opacity)); paint.Shader = shader;
        return shader;
    }

    private (SKColor[] Colors, float[] Positions) GradientBuffers(int count)
    {
        if (count >= _gradientBuffers.Length)
            return (new SKColor[count], new float[count]);
        ref var buffers = ref _gradientBuffers[count];
        if (buffers.Colors == null)
            buffers = (new SKColor[count], new float[count]);
        return buffers;
    }

    private static SKRect Rect(Rect rect) => new((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom);
    private static SKColor ToSkiaColor(Color color) => new(color.R, color.G, color.B, color.A);
    private static SKColor Color(Color color, double opacity) => new(color.R, color.G, color.B, Alpha(opacity));
    private static byte Alpha(double opacity) => (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);
    private static SKPath[] CreateSymbols()
    {
        var paths = new SKPath[(int)ShapeKind.Mixed];
        for (int i = 0; i < paths.Length; i++)
        {
            var path = paths[i] = new SKPath();
            if (i == (int)ShapeKind.Circle) { path.AddCircle(0, 0, 1); continue; }
            var points = SymbolGeometry.Points((ShapeKind)i);
            path.MoveTo((float)points[0].X, (float)points[0].Y);
            for (int p = 1; p < points.Length; p++) path.LineTo((float)points[p].X, (float)points[p].Y);
            path.Close();
        }
        return paths;
    }

    public void Dispose()
    {
        foreach (var image in _images.Keys) SkiaImageCache.Release(image);
        _images.Clear(); _unusedImages.Clear(); _recorder.Dispose(); _fill.Dispose(); _stroke.Dispose(); _path.Dispose();
    }
}

internal sealed class ScenePictureOperation(Rect bounds, SKPicture picture) : ICustomDrawOperation
{
    private static int _gpuObserved;
    private static long _renderedPictures;
    internal static bool GpuObserved => Volatile.Read(ref _gpuObserved) != 0;
    internal static long RenderedPictures => Interlocked.Read(ref _renderedPictures);
    public Rect Bounds { get; } = bounds;
    public bool HitTest(Point point) => false;
    public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
    public void Render(ImmediateDrawingContext context)
    {
        var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (feature == null) return;
        using var lease = feature.Lease();
        if (lease.GrContext != null) Volatile.Write(ref _gpuObserved, 1);
        Interlocked.Increment(ref _renderedPictures);
        if (lease.CurrentOpacity >= 1) lease.SkCanvas.DrawPicture(picture);
        else
        {
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp(Math.Round(lease.CurrentOpacity * 255), 0, 255)) };
            lease.SkCanvas.DrawPicture(picture, paint);
        }
    }
    public void Dispose() => picture.Dispose();
}
