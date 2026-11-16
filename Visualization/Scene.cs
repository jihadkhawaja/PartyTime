using System;
using System.Diagnostics;
using System.Linq;
using Vector3 = System.Numerics.Vector3;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using PartyTime.Audio;
using PartyTime.Settings;

namespace PartyTime.Visualization;

internal sealed partial class Scene
{
    private static readonly Color[][] Palettes =
    [
        [Rgb(105, 81, 255), Rgb(27, 224, 255), Rgb(255, 48, 171), Rgb(153, 58, 255)],
        [Rgb(255, 68, 110), Rgb(255, 179, 55), Rgb(255, 95, 42), Rgb(223, 68, 195)],
        [Rgb(68, 255, 178), Rgb(31, 201, 249), Rgb(110, 117, 255), Rgb(133, 255, 119)]
    ];

    private float[] _bands = new float[AudioAnalyzer.BandCount];
    private readonly ReactionState[] _reactions = [new(), new(), new(), new(), new()];
    private readonly Color[] _colors = new Color[4];
    private double _level;
    private double _drive;
    private double _phase;
    private double _beatAge;
    private double _pulse;
    private double Intensity => _settings.Intensity / 100.0;
    private double Size => _settings.Size / 100.0;
    private float _beatStrength;
    private VisualizerSettings _settings = new();
    private FrequencyRange _range = FrequencyRange.Full;
    private Color[] _stops = Palettes[0];
    private readonly Vector3[] _blendedStops = new Vector3[4];
    private bool _hasScreenColors;
    public bool BaseVisible { get; private set; }

    public void ApplySettings(VisualizerSettings settings)
    {
        _settings = settings;
        if (!settings.CursorTrailEnabled)
            ClearCursorTrail();
        ApplyImages(settings.Images);
        var range = settings.GetRange();
        if (range != _range)
        {
            _range = range;
            _reactions[(int)AudioReaction.Focus] = new();
        }

        if (settings.Theme == ColorTheme.Custom)
        {
            VisualizerSettings.TryReadColor(settings.FirstColor, out Color first);
            VisualizerSettings.TryReadColor(settings.SecondColor, out Color second);
            _stops = [first, Mix(first, second, 0.4), second, Mix(second, first, 0.4)];
        }
        else
        {
            _stops = Palettes[(int)settings.Theme];
        }
    }

    public bool Update(AudioFrame audio, long now, double elapsed, Color[]? screenColors)
    {
        double response = _settings.Response / 100.0;
        bool fresh = audio.Timestamp != 0 && Stopwatch.GetElapsedTime(audio.Timestamp, now).TotalSeconds < 0.14;
        bool beat = false;
        for (int i = 0; i < _reactions.Length; i++)
        {
            var signal = i switch { 0 => audio.Focused, 1 => audio.Full, 2 => audio.Low, 3 => audio.Mid, _ => audio.High };
            beat |= _reactions[i].Update(signal, now, elapsed, response,
                fresh && (i != (int)AudioReaction.Focus || signal.Range == _range)) && i == (int)AudioReaction.Full;
        }
        SelectReaction(AudioReaction.Full);
        if (beat)
            _phase += 0.17 + _beatStrength * 0.14;
        _phase = (_phase + elapsed * (0.18 + _level * 0.45)) % 4;
        BaseVisible = Intensity > 0 && ((_settings.Style != VisualStyle.None && _reactions[(int)_settings.BarsReaction].Visible)
            || (_settings.Circle != CircleStyle.None && _reactions[(int)_settings.CircleReaction].Visible)
            || (_settings.Border != BorderStyle.None && _reactions[(int)_settings.BorderReaction].Visible)
            || (_settings.Overlay != OverlayStyle.None && _reactions[(int)_settings.EffectsReaction].Visible)
            || (_settings.ShapesEnabled && _settings.ShapeOpacity > 0 && _reactions[(int)_settings.ShapesReaction].Visible)
            || _images.Any(image => image.Settings.Opacity > 0 && _reactions[(int)image.Settings.Reaction].Visible
                && (image.Settings.Layout != ImageLayout.ReplaceDots || _settings.Circle == CircleStyle.DotHalo || _settings.Overlay == OverlayStyle.Sparks)));
        if (!BaseVisible && !HasCursorTrail)
            return false;

        bool useScreen = screenColors != null && _settings.ColorSource != ColorSource.Palette;
        if (useScreen)
        {
            double screenAmount = _settings.ColorSource == ColorSource.Screen ? 1 : _settings.ScreenBlend / 100.0;
            float follow = _hasScreenColors ? (float)(1 - Math.Exp(-elapsed / 0.28)) : 1;
            // Preserve fractional color changes so short frame intervals can still reach the target.
            for (int i = 0; i < 4; i++)
            {
                Color target = Mix(_stops[i], screenColors![i], screenAmount);
                _blendedStops[i] = Vector3.Lerp(_blendedStops[i], new Vector3(target.R, target.G, target.B), follow);
            }
        }
        _hasScreenColors = useScreen;
        for (int i = 0; i < 4; i++)
        {
            double sample = ((_settings.AnimateColors ? _phase : 0) + i) % 4;
            int index = (int)sample;
            int next = (index + 1) % 4;
            if (useScreen)
            {
                var color = Vector3.Lerp(_blendedStops[index], _blendedStops[next], (float)(sample - index));
                _colors[i] = Rgb((byte)MathF.Round(color.X), (byte)MathF.Round(color.Y), (byte)MathF.Round(color.Z));
            }
            else
            {
                _colors[i] = Mix(_stops[index], _stops[next], sample - index);
            }
        }
        return true;
    }

    public void Draw(DrawingContext drawing, double width, double height, Point desktopOrigin = default, double dpiX = 1, double dpiY = 1)
        => Draw(new AvaloniaSceneDrawing(drawing), width, height, desktopOrigin, dpiX, dpiY);

    public void Draw(ISceneDrawing drawing, double width, double height, Point desktopOrigin = default, double dpiX = 1, double dpiY = 1)
    {
        DrawBase(drawing, width, height);
        DrawCursor(drawing, width, height, desktopOrigin, dpiX, dpiY);
    }

    public void DrawBase(ISceneDrawing drawing, double width, double height)
    {
        double shortSide = Math.Min(width, height);
        if (shortSide <= 0)
            return;
        drawing.SetImages(_imageSources);

        var center = new Point(width / 2, height / 2);
        if (SelectReaction(_settings.EffectsReaction))
        {
            double depth = shortSide * Size * (0.045 + _drive * 0.067 + _pulse * 0.047);
            DrawOverlay(drawing, width, height, shortSide, depth);
        }
        if (SelectReaction(_settings.BorderReaction))
        {
            double depth = shortSide * Size * (0.045 + _drive * 0.067 + _pulse * 0.047);
            DrawBorder(drawing, width, height, shortSide, depth);
        }
        if (SelectReaction(_settings.BarsReaction))
        {
            double depth = shortSide * Size * (0.045 + _drive * 0.067 + _pulse * 0.047);
            switch (_settings.Style)
            {
                case VisualStyle.EdgeBars:
                    DrawSpectrum(drawing, width, height, depth, top: true, _colors[0]);
                    DrawSpectrum(drawing, width, height, depth, top: false, _colors[2]);
                    break;
                case VisualStyle.CenterBars:
                    DrawCenterBars(drawing, width, height);
                    break;
                case VisualStyle.Wave:
                    DrawWave(drawing, width, height);
                    break;
            }
        }
        if (SelectReaction(_settings.CircleReaction))
            DrawCircle(drawing, center, shortSide * Size);
        DrawShapes(drawing, width, height, shortSide);
        DrawImages(drawing, width, height, shortSide);
    }

    public void DrawCursor(ISceneDrawing drawing, double width, double height, Point desktopOrigin = default, double dpiX = 1, double dpiY = 1)
    {
        if (HasCursorTrail && width > 0 && height > 0)
            DrawCursorTrail(drawing, width, height, desktopOrigin, dpiX, dpiY);
    }

    private bool SelectReaction(AudioReaction reaction)
    {
        var state = _reactions[(int)reaction];
        _level = state.Level;
        _drive = state.Drive;
        _pulse = state.Pulse;
        _beatAge = state.BeatAge;
        _beatStrength = state.BeatStrength;
        _bands = state.Bands;
        return state.Visible;
    }

    private sealed class ReactionState
    {
        public readonly float[] Bands = new float[AudioAnalyzer.BandCount];
        public double Level, Drive, Pulse, BeatAge = 1;
        public float BeatStrength;
        private long _beatTimestamp;
        public bool Visible => Math.Max(Level, Pulse) >= 0.014;

        public bool Update(AudioSignal audio, long now, double elapsed, double response, bool fresh)
        {
            Level = Follow(Level, fresh ? Math.Min(1, audio.Level * response) : 0, elapsed, 0.035, 0.18);
            Drive = Follow(Drive, fresh ? Math.Min(1, audio.Drive * response) : 0, elapsed, 0.025, 0.15);
            for (int i = 0; i < Bands.Length; i++)
                Bands[i] = (float)Follow(Bands[i], fresh ? Math.Min(1, audio.Bands[i] * response) : 0,
                    elapsed, 0.045, 0.14);
            bool beat = fresh && audio.BeatTimestamp != 0 && audio.BeatTimestamp != _beatTimestamp;
            if (beat)
            {
                _beatTimestamp = audio.BeatTimestamp;
                BeatStrength = audio.BeatStrength;
            }
            BeatAge = _beatTimestamp == 0 ? 1 : Stopwatch.GetElapsedTime(_beatTimestamp, now).TotalSeconds;
            Pulse = Math.Min(1, BeatStrength * response) * Math.Exp(-Math.Max(0, BeatAge) / 0.12);
            return beat;
        }
    }

    private void DrawGlow(ISceneDrawing drawing, double width, double height, double depth)
    {
        double strength = Intensity * Math.Min(0.89, 0.33 * _level + 0.44 * _drive + 0.47 * _pulse);
        double rail = Math.Min(1 + _drive * 1.5 + _pulse * 2.4, depth);
        for (int edge = 0; edge < 4; edge++)
        {
            Color start = _colors[edge];
            Color end = _colors[(edge + 1) % 4];
            Color glow = Mix(start, end, 0.45);
            DrawEdge(drawing, edge, width, height, depth, rail, strength, start, end, glow);
        }
    }

    private void DrawBeatRipple(ISceneDrawing drawing, double width, double height, double depth)
    {
        double rail = Math.Min(1 + _drive * 1.5 + _pulse * 2.4, depth);
        if (_beatAge < 0.45 && Math.Min(width, height) > 2 * (rail + depth))
        {
            double progress = Math.Clamp(_beatAge / 0.45, 0, 1);
            double inset = rail + depth * (0.1 + 0.72 * progress);
            double alpha = Intensity * _beatStrength * 0.58 * (1 - progress) * (1 - progress);
            var pen = new ImmutablePen(new ImmutableSolidColorBrush(Alpha(Mix(_colors[1], _colors[3], 0.5), alpha)), 1.4);
            drawing.DrawRoundedRectangle(null, pen, new Rect(inset, inset, width - 2 * inset, height - 2 * inset), 17);
        }
    }

    private static void DrawEdge(ISceneDrawing drawing, int edge, double width, double height,
        double depth, double rail, double strength, Color start, Color end, Color glow)
    {
        var (area, outer, inner, alongStart, alongEnd) = edge switch
        {
            0 => (new Rect(0, 0, width, depth), new Point(0, 0), new Point(0, 1), new Point(0, 0), new Point(1, 0)),
            1 => (new Rect(width - depth, 0, depth, height), new Point(1, 0), new Point(0, 0), new Point(0, 0), new Point(0, 1)),
            2 => (new Rect(0, height - depth, width, depth), new Point(0, 1), new Point(0, 0), new Point(1, 0), new Point(0, 0)),
            _ => (new Rect(0, 0, depth, height), new Point(0, 0), new Point(1, 0), new Point(0, 1), new Point(0, 0))
        };

        var vignette = SceneBrush.Linear(outer, inner, new(0, Alpha(glow, strength)), new(0.22, Alpha(glow, strength * 0.44)), new(0.58, Alpha(glow, strength * 0.12)), new(1, Alpha(glow, 0)));
        drawing.DrawRectangle(vignette, area);

        Rect strip = edge switch
        {
            0 => new Rect(0, 0, width, rail),
            1 => new Rect(width - rail, 0, rail, height),
            2 => new Rect(0, height - rail, width, rail),
            _ => new Rect(0, 0, rail, height)
        };
        double railAlpha = Math.Min(0.95, strength * 1.55);
        var highlight = SceneBrush.Linear(alongStart, alongEnd, new(0, Alpha(start, railAlpha)), new(0.53, Alpha(Mix(glow, Colors.White, 0.33), railAlpha * 0.9)), new(1, Alpha(end, railAlpha)));
        drawing.DrawRectangle(highlight, strip);
    }

    private void DrawSpectrum(ISceneDrawing drawing, double width, double height, double depth, bool top, Color color)
    {
        double span = width * (top ? 0.56 : 0.72);
        double step = span / _bands.Length;
        double barWidth = Math.Max(1, Math.Min(14, step * 0.42));
        double start = (width - span) / 2;
        double opacity = Intensity * (0.38 + _pulse * 0.40);
        var brush = SceneBrush.Linear(Alpha(color, opacity), Alpha(color, 0.10 * opacity),
            new Point(0, top ? 0 : 1), new Point(0, top ? 1 : 0));

        for (int i = 0; i < _bands.Length; i++)
        {
            double band = _bands[top ? _bands.Length - 1 - i : i];
            if (band < 0.035)
                continue;

            double barHeight = depth * (top ? 0.43 : 0.72) * band;
            double x = start + step * (i + 0.5) - barWidth / 2;
            double y = top ? 4 : height - 4 - barHeight;
            drawing.DrawRoundedRectangle(brush, null, new Rect(x, y, barWidth, barHeight), barWidth / 2);
        }
    }

    private void DrawOrbit(ISceneDrawing drawing, Point center, double unit, double opacity)
    {
        double radius = unit * (0.16 + _drive * 0.042 + _pulse * 0.044);
        double haloRadius = radius + unit * 0.07;
        double rim = radius / haloRadius;
        var halo = SceneBrush.Radial(new(rim - 0.2, Alpha(_colors[2], 0)), new(rim, Alpha(_colors[2], opacity * 0.43)), new(1, Alpha(_colors[2], 0)));
        drawing.DrawEllipse(halo, null, center, haloRadius);
        drawing.DrawEllipse(null, new ImmutablePen(SpectrumBrush(opacity), Math.Clamp(unit * 0.003, 1, 5)), center, radius);

        for (int i = 0; i < 4; i++)
        {
            var pen = new ImmutablePen(new ImmutableSolidColorBrush(Alpha(_colors[i], opacity * 0.94)),
                Math.Clamp(unit * 0.0035, 1, 6), lineCap: PenLineCap.Round);
            int first = i * _bands.Length / 4;
            int end = (i + 1) * _bands.Length / 4;
            for (int band = first; band < end; band++)
            {
                double angle = 2 * Math.PI * band / _bands.Length - Math.PI / 2 + _phase * 0.12;
                var (sin, cos) = Math.SinCos(angle);
                var direction = new Vector(cos, sin);
                double start = radius + unit * 0.013;
                double length = unit * (0.006 + _bands[band] * 0.10);
                drawing.DrawLine(pen, center + direction * start, center + direction * (start + length));
            }
        }
    }

    private void DrawCenterBars(ISceneDrawing drawing, double width, double height)
    {
        double span = width * 0.53 * Size;
        double step = span / _bands.Length;
        double barWidth = Math.Max(1, step * 0.53);
        var brush = SceneBrush.Linear(Alpha(_colors[0], Intensity * 0.85), Alpha(_colors[2], Intensity * 0.73),
            new Point(0, 0), new Point(0, 1));
        for (int i = 0; i < _bands.Length; i++)
        {
            double band = _bands[i];
            if (band < 0.03)
                continue;
            double barHeight = height * Size * band * (0.30 + _pulse * 0.12);
            double x = (width - span) / 2 + step * (i + 0.5) - barWidth / 2;
            drawing.DrawRoundedRectangle(brush, null, new Rect(x, (height - barHeight) / 2, barWidth, barHeight),
                barWidth / 2);
        }
    }

    private void DrawWave(ISceneDrawing drawing, double width, double height)
    {
        double span = width * Math.Min(0.93, 0.62 * Size);
        double start = (width - span) / 2;
        double step = span / (_bands.Length + 1);
        double center = height / 2;
        Span<(Point Control, Point End)> segments = stackalloc (Point, Point)[AudioAnalyzer.BandCount + 1];
        for (int i = 0; i < segments.Length; i++)
        {
            double y = i == _bands.Length ? center
                : center + (i % 2 == 0 ? 1 : -1) * _bands[i] * height * Size * (0.13 + _pulse * 0.08);
            double x = start + (i + 1) * step;
            segments[i] = (new Point(x - step / 2, y), new Point(x, y));
        }
        double opacity = Intensity * Math.Min(1, _level * 1.4 + _pulse * 0.55);
        double thickness = Math.Clamp(Math.Min(width, height) * 0.0035, 1.2, 7);
        drawing.DrawCurve(new Point(start, center), segments, new ImmutablePen(SpectrumBrush(opacity * 0.24), thickness * 3),
            new ImmutablePen(SpectrumBrush(opacity), thickness));
    }

    private ImmutableLinearGradientBrush SpectrumBrush(double opacity)
        => SceneBrush.Linear(Alpha(_colors[0], opacity), Alpha(_colors[2], opacity), new Point(0, 0), new Point(1, 1));

    private static double Follow(double current, double target, double elapsed, double attack, double release)
        => current + (target - current) * (1 - Math.Exp(-elapsed / (target > current ? attack : release)));

    private static Color Rgb(byte red, byte green, byte blue) => Color.FromRgb(red, green, blue);

    private static Color Alpha(Color color, double opacity)
        => Color.FromArgb((byte)Math.Round(255 * opacity), color.R, color.G, color.B);

    private static Color Mix(Color a, Color b, double amount) => Rgb(
        (byte)Math.Round(a.R + (b.R - a.R) * amount),
        (byte)Math.Round(a.G + (b.G - a.G) * amount),
        (byte)Math.Round(a.B + (b.B - a.B) * amount));
}
