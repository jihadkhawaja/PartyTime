using System;
using System.Windows;
using System.Windows.Media;
using PartyTime.Audio;
using PartyTime.Settings;

namespace PartyTime.Visualization;

internal sealed partial class Scene
{
    private void DrawBorder(ISceneDrawing drawing, double width, double height, double shortSide, double depth)
    {
        if (_settings.Border == BorderStyle.Glow)
        {
            DrawGlow(drawing, width, height, depth);
            return;
        }

        double opacity = Intensity * Math.Min(1, _level * 0.75 + _drive * 0.45 + _pulse * 0.55);
        double unit = shortSide * Size;
        double inset = shortSide * 0.016;
        var bounds = new Rect(inset, inset, width - 2 * inset, height - 2 * inset);
        double thickness = Math.Clamp(unit * (0.004 + _pulse * 0.004), 1.2, 8);

        switch (_settings.Border)
        {
            case BorderStyle.DoubleRail:
                DrawDoubleRail(drawing, bounds, unit, opacity, thickness);
                break;
            case BorderStyle.Corners:
                DrawCorners(drawing, bounds, unit, opacity, thickness);
                break;
            case BorderStyle.Marquee:
                DrawMarquee(drawing, bounds, unit, opacity, thickness);
                break;
        }
    }

    private void DrawDoubleRail(ISceneDrawing drawing, Rect bounds, double unit, double opacity, double thickness)
    {
        double radius = unit * 0.028;
        drawing.DrawRoundedRectangle(null, new Pen(SpectrumBrush(opacity), thickness), bounds, radius);
        double gap = unit * (0.021 + _drive * 0.017 + _pulse * 0.025);
        bounds.Inflate(-gap, -gap);
        drawing.DrawRoundedRectangle(null, new Pen(SpectrumBrush(opacity * 0.52), thickness * 0.65), bounds, radius);
    }

    private void DrawCorners(ISceneDrawing drawing, Rect bounds, double unit, double opacity, double thickness)
    {
        double length = unit * (0.11 + _drive * 0.05 + _pulse * 0.06);
        Span<Point> corners = [bounds.TopLeft, bounds.TopRight, bounds.BottomRight, bounds.BottomLeft];
        for (int i = 0; i < corners.Length; i++)
        {
            Point corner = corners[i];
            var horizontal = new Vector(i is 0 or 3 ? length : -length, 0);
            var vertical = new Vector(0, i < 2 ? length : -length);
            var pen = RoundedPen(_colors[i], opacity, thickness * 1.45);
            drawing.DrawLine(pen, corner + horizontal, corner);
            drawing.DrawLine(pen, corner, corner + vertical);
        }
    }

    private void DrawMarquee(ISceneDrawing drawing, Rect bounds, double unit, double opacity, double thickness)
    {
        double offset = _phase / 4;
        double dashLength = 0.46 + _drive * 0.18;
        Span<Point> corners = [bounds.TopLeft, bounds.TopRight, bounds.BottomRight, bounds.BottomLeft];
        for (int edge = 0; edge < corners.Length; edge++)
        {
            Point start = corners[edge];
            Vector side = corners[(edge + 1) % corners.Length] - start;
            int count = (int)Math.Clamp(side.Length / (unit * 0.105), 5, 32);
            Vector step = side / count;
            var pen = RoundedPen(_colors[edge], opacity, thickness);
            for (int i = -1; i < count; i++)
            {
                double from = Math.Max(0, i + offset);
                double to = Math.Min(count, i + offset + dashLength);
                if (from < to)
                    drawing.DrawLine(pen, start + step * from, start + step * to);
            }
        }
    }

    private void DrawCircle(ISceneDrawing drawing, Point center, double unit)
    {
        double opacity = Intensity * Math.Min(1, _level * 1.2 + _pulse * 0.6);
        switch (_settings.Circle)
        {
            case CircleStyle.Orbit:
                DrawOrbit(drawing, center, unit, opacity);
                break;
            case CircleStyle.PulseRings:
                DrawPulseRings(drawing, center, unit, opacity);
                break;
            case CircleStyle.RadialWave:
                DrawRadialWave(drawing, center, unit, opacity);
                break;
            case CircleStyle.DotHalo:
                DrawDotHalo(drawing, center, unit, opacity);
                break;
        }
    }

    private void DrawPulseRings(ISceneDrawing drawing, Point center, double unit, double opacity)
    {
        double thickness = Math.Clamp(unit * 0.004, 1.2, 6);
        for (int ring = 0; ring < 3; ring++)
        {
            double radius = unit * (0.09 + ring * 0.055 + _drive * 0.024 + _pulse * (0.03 + ring * 0.018));
            var pen = new Pen(new SolidColorBrush(Alpha(_colors[ring], opacity * (1 - ring * 0.19))), thickness);
            drawing.DrawEllipse(null, pen, center, radius);
        }
    }

    private void DrawRadialWave(ISceneDrawing drawing, Point center, double unit, double opacity)
    {
        Span<Point> points = stackalloc Point[AudioAnalyzer.BandCount];
        double baseRadius = unit * (0.14 + _pulse * 0.035);
        for (int i = 0; i < points.Length; i++)
            points[i] = CirclePoint(center, i, baseRadius + unit * 0.12 * _bands[i]);

        Span<(Point Control, Point End)> segments = stackalloc (Point, Point)[AudioAnalyzer.BandCount];
        Point previous = points[^1];
        for (int i = 0; i < segments.Length; i++)
        {
            Point current = points[i];
            segments[i] = (previous, previous + (current - previous) * 0.5);
            previous = current;
        }

        double thickness = Math.Clamp(unit * 0.004, 1.2, 6);
        drawing.DrawCurve(segments[^1].End, segments, new Pen(SpectrumBrush(opacity * 0.28), thickness * 3),
            new Pen(SpectrumBrush(opacity), thickness));
    }

    private void DrawDotHalo(ISceneDrawing drawing, Point center, double unit, double opacity)
    {
        double radius = unit * (0.15 + _drive * 0.03 + _pulse * 0.05);
        drawing.DrawEllipse(null, new Pen(SpectrumBrush(opacity * 0.30), Math.Clamp(unit * 0.002, 0.8, 3)), center, radius);
        for (int color = 0; color < _colors.Length; color++)
        {
            var brush = new SolidColorBrush(Alpha(_colors[color], opacity));
            int end = (color + 1) * _bands.Length / _colors.Length;
            for (int i = color * _bands.Length / _colors.Length; i < end; i++)
            {
                double band = _bands[i];
                double dotRadius = Math.Max(0.7, unit * (0.0025 + band * 0.008));
                drawing.DrawEllipse(brush, null, CirclePoint(center, i, radius + unit * band * 0.09), dotRadius);
            }
        }
    }

    private static Point CirclePoint(Point center, int band, double radius)
    {
        var (sin, cos) = Math.SinCos(Math.Tau * band / AudioAnalyzer.BandCount - Math.PI / 2);
        return new Point(center.X + cos * radius, center.Y + sin * radius);
    }

    private void DrawOverlay(ISceneDrawing drawing, double width, double height, double shortSide, double depth)
    {
        double opacity = Intensity * Math.Min(1, _level * 0.65 + _drive * 0.4 + _pulse * 0.65);
        double unit = shortSide * Size;
        switch (_settings.Overlay)
        {
            case OverlayStyle.BeatRipple:
                DrawBeatRipple(drawing, width, height, depth);
                break;
            case OverlayStyle.CornerBloom:
                DrawCornerBloom(drawing, width, height, unit, opacity);
                break;
            case OverlayStyle.Sparks:
                DrawSparks(drawing, width, height, unit, opacity);
                break;
            case OverlayStyle.LightSweep:
                DrawLightSweep(drawing, width, height, unit, opacity);
                break;
        }
    }

    private void DrawCornerBloom(ISceneDrawing drawing, double width, double height, double unit, double opacity)
    {
        double radius = unit * (0.31 + _drive * 0.16 + _pulse * 0.16);
        Span<Point> corners = [new(0, 0), new(width, 0), new(width, height), new(0, height)];
        for (int i = 0; i < corners.Length; i++)
        {
            Color color = _colors[i];
            var brush = new RadialGradientBrush(Alpha(color, opacity * 0.7), Alpha(color, 0));
            brush.GradientStops.Insert(1, new GradientStop(Alpha(color, opacity * 0.18), 0.56));
            drawing.DrawEllipse(brush, null, corners[i], radius);
        }
    }

    private void DrawSparks(ISceneDrawing drawing, double width, double height, double unit, double opacity)
    {
        double phase = Math.Tau * _phase / 4;
        for (int color = 0; color < _colors.Length; color++)
        {
            var brush = new SolidColorBrush(Alpha(Mix(_colors[color], Colors.White, 0.25), opacity * 0.9));
            for (int spark = 0; spark < 12; spark++)
            {
                // Distribute the points evenly without storing particles or re-seeding each frame.
                int index = color * 12 + spark + 1;
                var (sin, cos) = Math.SinCos(phase + index * 2.4);
                double x = 0.07 + 0.86 * (index * 0.618034 % 1) + 0.025 * cos;
                double y = 0.07 + 0.86 * (index * 0.754878 % 1) + 0.05 * sin;
                double radius = Math.Max(0.65, unit * (0.0016 + _pulse * 0.0024) * (1 + 0.35 * sin));
                drawing.DrawEllipse(brush, null, new Point(width * x, height * y), radius);
            }
        }
    }

    private void DrawLightSweep(ISceneDrawing drawing, double width, double height, double unit, double opacity)
    {
        double bandHeight = unit * (0.18 + _pulse * 0.14);
        double y = (height + bandHeight) * (_phase / 4) - bandHeight;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(Alpha(_colors[0], 0), 0));
        brush.GradientStops.Add(new GradientStop(Alpha(_colors[0], opacity * 0.19), 0.40));
        brush.GradientStops.Add(new GradientStop(Alpha(_colors[2], opacity * 0.40), 0.72));
        brush.GradientStops.Add(new GradientStop(Alpha(_colors[2], 0), 1));
        drawing.DrawRectangle(brush, new Rect(0, y, width, bandHeight));
    }

    private static Pen RoundedPen(Color color, double opacity, double thickness)
        => new(new SolidColorBrush(Alpha(color, opacity)), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
}
