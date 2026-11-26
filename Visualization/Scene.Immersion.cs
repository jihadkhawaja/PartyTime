using System;
using Avalonia;
using Avalonia.Media;
using PartyTime.Audio;
using PartyTime.Settings;

namespace PartyTime.Visualization;

internal sealed partial class Scene
{
    private const int HistoryLength = 18;
    private const double HistoryInterval = 0.095;
    private const int TunnelRingCount = 12;
    private readonly float[,] _spectrumHistory = new float[HistoryLength, AudioAnalyzer.BandCount];
    private int _historyNext, _historyCount;
    private double _historyElapsed, _tunnelTravel;

    private bool MusicLayerEnabled => _settings.MusicEffect != MusicEffect.None && _settings.MusicStrength > 0;
    private bool CinemaLayerEnabled => (_settings.AmbientLighting || _settings.CinemaVignette) && _settings.CinemaStrength > 0;

    private void ResetSpectrumHistory()
    {
        _historyNext = _historyCount = 0;
        _historyElapsed = 0;
    }

    private void UpdateSpectrumHistory(ReactionState state, double elapsed)
    {
        if (!state.Visible || !MusicLayerEnabled || Intensity == 0)
        {
            ResetSpectrumHistory();
            return;
        }

        _historyElapsed += elapsed;
        while (_historyElapsed >= HistoryInterval)
        {
            _historyElapsed -= HistoryInterval;
            for (int band = 0; band < state.Bands.Length; band++)
                _spectrumHistory[_historyNext, band] = state.Bands[band];
            _historyNext = (_historyNext + 1) % HistoryLength;
            _historyCount = Math.Min(HistoryLength, _historyCount + 1);
        }
    }

    private void DrawMusicLayer(ISceneDrawing drawing, double width, double height, double unit)
    {
        double strength = Intensity * _settings.MusicStrength / 100 * Math.Min(1, _level * 1.55 + _drive * 0.46 + _pulse * 0.35);
        switch (_settings.MusicEffect)
        {
            case MusicEffect.SpectrumWaterfall:
                DrawSpectrumWaterfall(drawing, width, height, unit, strength);
                break;
            case MusicEffect.WarpTunnel:
                DrawWarpTunnel(drawing, width, height, unit, strength);
                break;
            case MusicEffect.StageBeams:
                DrawStageBeams(drawing, width, height, unit, strength);
                break;
        }
    }

    private void DrawSpectrumWaterfall(ISceneDrawing drawing, double width, double height, double unit, double strength)
    {
        double origin = height * 0.9;
        double travel = height * 0.53;
        double frontSpan = width * Math.Min(0.94, 0.79 * Size);
        double lineWidth = Math.Clamp(unit * 0.003, 0.8, 4);
        Span<(Point Control, Point End)> curve = stackalloc (Point, Point)[AudioAnalyzer.BandCount + 1];

        // Paint the oldest, most distant ridge first. The front ridge always uses the current spectrum.
        for (int age = _historyCount; age >= 0; age--)
        {
            double distance = age == 0 ? 0 : Math.Min(1, (age - 0.6 + _historyElapsed / HistoryInterval) / HistoryLength);
            double perspective = 1 - distance * 0.71;
            double span = frontSpan * perspective;
            double y = origin - travel * distance;
            double x = (width - span) / 2;
            double step = span / curve.Length;
            double amplitude = height * Math.Min(0.33, 0.23 * Size) * perspective;
            var start = new Point(x, y);
            var previous = start;
            int row = (_historyNext - age + HistoryLength) % HistoryLength;
            for (int band = 0; band < _bands.Length; band++)
            {
                double value = age == 0 ? _bands[band] : _spectrumHistory[row, band];
                var point = new Point(x + (band + 0.5) * step, y - amplitude * value);
                curve[band] = (previous, previous + (point - previous) * 0.5);
                previous = point;
            }
            curve[^1] = (previous, new Point(x + span, y));

            double opacity = strength * (1 - distance) * (age == 0 ? 0.92 : 0.58);
            drawing.DrawHorizontalGradientCurve(start, curve, Alpha(Mix(_colors[0], _colors[2], distance), opacity),
                Alpha(Mix(_colors[1], _colors[3], distance), opacity), lineWidth * perspective,
                Alpha(_colors[2], opacity * 0.21), lineWidth * 3 * perspective);
        }
    }

    private void DrawWarpTunnel(ISceneDrawing drawing, double width, double height, double unit, double strength)
    {
        const int sides = 8;
        var center = new Point(width / 2, height / 2);
        Span<Point> points = stackalloc Point[sides];
        double phase = _tunnelTravel % 1;
        for (int ring = 0; ring < TunnelRingCount; ring++)
        {
            double depth = (ring + phase) / TunnelRingCount;
            double radius = unit * Math.Min(1.02, Size * 0.78) * (0.055 + depth * depth)
                * (1 + _pulse * 0.12 * depth);
            double turn = depth * 0.63 + Math.Sin(_phase * Math.Tau / 4) * 0.23;
            double aspect = Math.Clamp(width / height, 0.65, 1.9);
            for (int vertex = 0; vertex < sides; vertex++)
            {
                int band = vertex * (_bands.Length / sides);
                double detail = 1 + Math.Max(_bands[band], _bands[band + 1]) * 0.23;
                var (sin, cos) = Math.SinCos(turn + (vertex + 0.5) * Math.Tau / sides);
                points[vertex] = center + new Vector(cos * aspect, sin) * (radius * detail);
            }

            double fade = Math.Min(1, Math.Min(depth * 8, (1 - depth) * 7));
            int colorIndex = (ring - (int)_tunnelTravel + TunnelRingCount) % _colors.Length;
            Color color = _colors[colorIndex];
            double opacity = strength * fade * (0.50 + depth * 0.42);
            double thickness = Math.Clamp(unit * (0.0012 + depth * 0.0025), 0.75, 5);
            drawing.DrawOutlinedPolygon(points, Alpha(color, opacity * 0.21), thickness * 3.6,
                Alpha(Mix(color, Colors.White, 0.2), opacity), thickness);
        }
    }

    private void DrawStageBeams(ISceneDrawing drawing, double width, double height, double unit, double strength)
    {
        const int beamCount = 5;
        double motion = _phase * Math.Tau / 4;
        double floorDepth = unit * 0.18;
        var floor = SceneBrush.Linear(Alpha(_colors[2], 0), Alpha(_colors[2], strength * 0.27),
            new Point(0, 0), new Point(0, 1));
        drawing.DrawRectangle(floor, new Rect(0, height - floorDepth, width, floorDepth));
        Span<Point> beam = stackalloc Point[4];
        for (int i = 0; i < beamCount; i++)
        {
            double position = (i + 1) / (beamCount + 1.0);
            double band = 0;
            for (int j = i * _bands.Length / beamCount; j < (i + 1) * _bands.Length / beamCount; j++)
                band = Math.Max(band, _bands[j]);
            double baseX = width * position;
            double farX = width * (0.5 + (position - 0.5) * 0.67 + Math.Sin(motion + i * 1.6) * 0.22);
            double farY = height * Math.Max(0.02, 0.43 - Size * (0.18 + band * 0.16) - _pulse * 0.10);
            double spread = unit * (0.032 + band * 0.095 + _pulse * 0.035) * Size;
            double baseWidth = unit * 0.007;
            beam[0] = new Point(baseX - baseWidth, height);
            beam[1] = new Point(farX - spread, farY);
            beam[2] = new Point(farX + spread, farY);
            beam[3] = new Point(baseX + baseWidth, height);

            Color color = _colors[i % _colors.Length];
            double opacity = strength * (0.15 + band * 0.42 + _pulse * 0.14);
            var light = SceneBrush.Linear(new Point(0, 1), new Point(0, 0),
                new(0, Alpha(Mix(color, Colors.White, 0.28), opacity)),
                new(0.38, Alpha(color, opacity * 0.65)), new(1, Alpha(color, 0)));
            drawing.DrawPolygon(beam, light, null);
            drawing.DrawGlow(new Point(baseX, height - unit * 0.018), unit * (0.012 + band * 0.019), color, opacity);
        }
    }

    private void DrawCinema(ISceneDrawing drawing, double width, double height, double unit, Color[]? ambientColors)
    {
        double strength = Intensity * _settings.CinemaStrength / 100 * Math.Min(1, _level * 1.3 + _drive * 0.55 + _pulse * 0.28);
        double depth = unit * (0.12 + 0.055 * _drive + 0.025 * _pulse);
        if (_settings.CinemaVignette)
            for (int edge = 0; edge < 4; edge++)
                DrawCinemaEdge(drawing, edge, width, height, depth, Colors.Black, strength * 0.73);

        if (!_settings.AmbientLighting)
            return;
        var colors = _settings.UsesAmbientScreenColors && ambientColors != null ? ambientColors : _stops;
        for (int edge = 0; edge < colors.Length; edge++)
        {
            var color = colors[edge];
            DrawCinemaEdge(drawing, edge, width, height, depth * 0.76, Mix(color, colors[(edge + 1) % 4], 0.5), strength * 0.40);
            var corner = edge switch
            {
                0 => new Point(0, 0),
                1 => new Point(width, 0),
                2 => new Point(width, height),
                _ => new Point(0, height)
            };
            var bloom = SceneBrush.Radial(new(0, Alpha(color, strength * 0.6)),
                new(0.42, Alpha(color, strength * 0.24)), new(1, Alpha(color, 0)));
            drawing.DrawEllipse(bloom, null, corner, depth * 1.45);
        }
    }

    private static void DrawCinemaEdge(ISceneDrawing drawing, int edge, double width, double height, double depth, Color color, double opacity)
    {
        var (area, outer, inner) = edge switch
        {
            0 => (new Rect(0, 0, width, depth), new Point(0, 0), new Point(0, 1)),
            1 => (new Rect(width - depth, 0, depth, height), new Point(1, 0), new Point(0, 0)),
            2 => (new Rect(0, height - depth, width, depth), new Point(0, 1), new Point(0, 0)),
            _ => (new Rect(0, 0, depth, height), new Point(0, 0), new Point(1, 0))
        };
        drawing.DrawRectangle(SceneBrush.Linear(outer, inner,
            new(0, Alpha(color, opacity)), new(0.38, Alpha(color, opacity * 0.37)), new(1, Alpha(color, 0))), area);
    }
}
