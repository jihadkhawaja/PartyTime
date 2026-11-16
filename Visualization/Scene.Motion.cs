using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using PartyTime.Settings;

namespace PartyTime.Visualization;

internal sealed partial class Scene
{
    private readonly List<(Point Position, long Time)> _cursorPoints = [];
    private long _cursorNow;
    private Rect _cursorBounds;
    private bool _cursorBoundsDirty;
    private Point? _lastPointerPosition;
    private const int MaximumCursorPoints = 192;
    public bool HasCursorTrail => _settings.CursorTrailEnabled && Intensity > 0 && _cursorPoints.Count > 0;

    public bool CursorIntersects(Rect desktopBounds, double dpiScale = 1)
    {
        if (!HasCursorTrail)
            return false;
        if (_cursorBoundsDirty)
        {
            var bounds = new Rect(_cursorPoints[0].Position, new Size(0.01, 0.01));
            foreach (var sample in _cursorPoints) bounds = bounds.Union(new Rect(sample.Position, new Size(0.01, 0.01)));
            _cursorBounds = bounds; _cursorBoundsDirty = false;
        }
        return desktopBounds.Intersects(_cursorBounds.Inflate(_settings.CursorTrailSize * 4 * dpiScale));
    }

    public void ClearCursorTrail()
    {
        _cursorPoints.Clear();
        _cursorBoundsDirty = true;
        _lastPointerPosition = null;
    }

    public void UpdatePointer(Point? position, long now)
    {
        _cursorNow = now;
        if (!_settings.CursorTrailEnabled)
        {
            ClearCursorTrail();
            return;
        }
        double lifetime = _settings.CursorTrailLength / 1000.0;
        int expired = 0;
        while (expired < _cursorPoints.Count && Stopwatch.GetElapsedTime(_cursorPoints[expired].Time, now).TotalSeconds >= lifetime)
            expired++;
        if (expired > 0) { _cursorPoints.RemoveRange(0, expired); _cursorBoundsDirty = true; }
        if (position is not { } current || !double.IsFinite(current.X) || !double.IsFinite(current.Y))
            return;
        if (_lastPointerPosition is { } last && ((Vector)(current - last)).Length < 2)
            return;
        _lastPointerPosition = current;
        _cursorBoundsDirty = true;
        if (_cursorPoints.Count == 0)
        {
            _cursorPoints.Add((current, now));
            return;
        }
        var previous = _cursorPoints[^1];
        Vector delta = current - previous.Position;
        if (delta.Length < 2)
            return;
        int steps = Math.Clamp((int)Math.Ceiling(delta.Length / 8), 1, 24);
        for (int i = 1; i <= steps; i++)
        {
            double amount = i / (double)steps;
            _cursorPoints.Add((previous.Position + delta * amount, previous.Time + (long)((now - previous.Time) * amount)));
        }
        if (_cursorPoints.Count > MaximumCursorPoints)
            _cursorPoints.RemoveRange(0, _cursorPoints.Count - MaximumCursorPoints);
    }

    private void DrawCursorTrail(ISceneDrawing drawing, double width, double height, Point origin, double dpiX, double dpiY)
    {
        if (!_settings.CursorTrailEnabled || _cursorPoints.Count == 0)
            return;
        SelectReaction(_settings.CursorReaction);
        double lifetime = _settings.CursorTrailLength / 1000.0;
        double unit = Math.Clamp(Math.Min(width, height) / 720, 0.25, 2);
        double margin = _settings.CursorTrailSize * 4;
        var viewport = new Rect(-margin, -margin, width + 2 * margin, height + 2 * margin);
        Point Local(Point point) => new((point.X - origin.X) / dpiX, (point.Y - origin.Y) / dpiY);
        for (int i = 0; i < _cursorPoints.Count; i++)
        {
            var sample = _cursorPoints[i];
            double remaining = Math.Clamp(1 - Stopwatch.GetElapsedTime(sample.Time, _cursorNow).TotalSeconds / lifetime, 0, 1);
            if (remaining <= 0)
                continue;
            double opacity = Intensity * remaining * remaining * 0.85;
            double radius = _settings.CursorTrailSize * unit * (0.2 + remaining * 0.55) * (0.8 + _pulse * 0.5 + _drive * 0.2);
            var point = Local(sample.Position);
            Color color = Mix(_colors[i % 4], _colors[(i + 1) % 4], remaining);
            if (_settings.CursorTrail == CursorTrailStyle.Ribbon)
            {
                if (i > 0)
                {
                    var start = Local(_cursorPoints[i - 1].Position);
                    if (viewport.Intersects(new Rect(start, point)))
                        drawing.DrawColoredLine(start, point, color, opacity, Math.Max(0.7, radius));
                }
                else if (viewport.Contains(point))
                    drawing.DrawEllipse(new ImmutableSolidColorBrush(Alpha(color, opacity)), null, point, Math.Max(0.5, radius / 3));
                continue;
            }
            if (!viewport.Contains(point))
                continue;
            if (_settings.CursorTrail == CursorTrailStyle.Shapes)
            {
                DrawShape(drawing, ResolveShape(_settings.CursorShape, i), point, radius,
                    _phase + i * 0.18, color, opacity, filled: true);
            }
            else
            {
                drawing.DrawGlow(point, radius, color, opacity);
            }
        }
    }

    private void DrawShapes(ISceneDrawing drawing, double width, double height, double shortSide)
    {
        if (!_settings.ShapesEnabled || !SelectReaction(_settings.ShapesReaction))
            return;
        double opacity = Intensity * _settings.ShapeOpacity / 100.0 * Math.Min(1, _level * 1.1 + _pulse * 0.6);
        double phase = Math.Tau * _phase / 4;
        for (int i = 0; i < _settings.ShapeCount; i++)
        {
            double fraction = (i + 0.5) / _settings.ShapeCount;
            double angle = Math.Tau * fraction + phase;
            double x = 0.08 + 0.84 * ((i + 1) * 0.618034 % 1) + Math.Sin(phase + i * 1.7) * 0.025;
            double y = 0.08 + 0.84 * ((i + 1) * 0.754878 % 1) + Math.Cos(phase + i * 2.4) * 0.04;
            var center = new Point(width * x, height * y);
            double alpha = opacity;
            switch (_settings.ShapeMotion)
            {
                case ShapeMotion.Orbit:
                    double orbit = shortSide * Size * (0.22 + _drive * 0.05 + _pulse * 0.06);
                    center = new Point(width / 2 + Math.Cos(angle) * orbit, height / 2 + Math.Sin(angle) * orbit);
                    break;
                case ShapeMotion.Rain:
                    center = new Point(width * x, height * (((i + 1) * 0.754878 + _phase / 4) % 1));
                    break;
                case ShapeMotion.Burst:
                    if (_beatAge >= 0.8)
                        return;
                    double progress = Math.Clamp(_beatAge / 0.8, 0, 1);
                    double burst = shortSide * Size * (0.08 + progress * 0.5);
                    center = new Point(width / 2 + Math.Cos(angle) * burst, height / 2 + Math.Sin(angle) * burst);
                    alpha *= 1 - progress;
                    break;
                case ShapeMotion.Spiral:
                    angle = phase * 2 + fraction * Math.Tau * 2;
                    double spiral = shortSide * Size * (0.04 + fraction * 0.34 + _pulse * 0.03);
                    center = new Point(width / 2 + Math.Cos(angle) * spiral, height / 2 + Math.Sin(angle) * spiral);
                    break;
            }
            double band = _bands[i * _bands.Length / _settings.ShapeCount];
            double radius = shortSide * _settings.ShapeSize / 100.0 * (0.45 + band * 0.35 + _pulse * 0.35);
            DrawShape(drawing, ResolveShape(_settings.Shape, i), center, radius, phase * 0.3 + i * 0.4,
                _colors[i % 4], alpha, _settings.FilledShapes);
        }
    }

    private static ShapeKind ResolveShape(ShapeKind kind, int index)
        => kind == ShapeKind.Mixed ? (ShapeKind)(index % (int)ShapeKind.Mixed) : kind;

    private static void DrawShape(ISceneDrawing drawing, ShapeKind shape, Point center, double radius,
        double rotation, Color color, double opacity, bool filled)
    {
        if (radius <= 0 || opacity <= 0)
            return;
        drawing.DrawSymbol(shape, center, radius, rotation, color, opacity, filled);
    }
}
