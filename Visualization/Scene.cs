using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using PartyTime.Audio;
using PartyTime.Settings;

namespace PartyTime.Visualization;

internal sealed class Scene
{
    private static readonly Color[][] Palettes =
    [
        [Rgb(105, 81, 255), Rgb(27, 224, 255), Rgb(255, 48, 171), Rgb(153, 58, 255)],
        [Rgb(255, 68, 110), Rgb(255, 179, 55), Rgb(255, 95, 42), Rgb(223, 68, 195)],
        [Rgb(68, 255, 178), Rgb(31, 201, 249), Rgb(110, 117, 255), Rgb(133, 255, 119)]
    ];

    private readonly float[] _bands = new float[AudioAnalyzer.BandCount];
    private readonly Color[] _colors = new Color[4];
    private double _level;
    private double _drive;
    private double _phase;
    private double _beatAge;
    private double _pulse;
    private double Intensity => _settings.Intensity / 100.0;
    private double Size => _settings.Size / 100.0;
    private float _beatStrength;
    private long _beatTimestamp;
    private VisualizerSettings _settings = new();
    private FrequencyRange _range = FrequencyRange.Full;
    private Color[] _stops = Palettes[0];
    private readonly Color[] _blendedStops = new Color[4];
    private bool _hasScreenColors;

    public void ApplySettings(VisualizerSettings settings)
    {
        _settings = settings;
        var range = settings.GetRange();
        if (range != _range)
        {
            _range = range;
            _level = _drive = 0;
            _beatStrength = 0;
            _beatTimestamp = 0;
            Array.Clear(_bands);
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
        bool fresh = audio.Range == _range && audio.Timestamp != 0
            && Stopwatch.GetElapsedTime(audio.Timestamp, now).TotalSeconds < 0.14;
        double response = _settings.Response / 100.0;
        _level = Follow(_level, fresh ? Math.Min(1, audio.Level * response) : 0, elapsed, 0.035, 0.18);
        _drive = Follow(_drive, fresh ? Math.Min(1, audio.Drive * response) : 0, elapsed, 0.025, 0.15);
        for (int i = 0; i < _bands.Length; i++)
            _bands[i] = (float)Follow(_bands[i], fresh ? Math.Min(1, audio.Bands[i] * response) : 0, elapsed, 0.045, 0.14);

        if (fresh && audio.BeatTimestamp != 0 && audio.BeatTimestamp != _beatTimestamp)
        {
            _beatTimestamp = audio.BeatTimestamp;
            _beatStrength = audio.BeatStrength;
            _phase += 0.17 + _beatStrength * 0.14;
        }

        _beatAge = _beatTimestamp == 0 ? 1 : Stopwatch.GetElapsedTime(_beatTimestamp, now).TotalSeconds;
        _pulse = Math.Min(1, _beatStrength * response) * Math.Exp(-Math.Max(0, _beatAge) / 0.12);
        _phase = (_phase + elapsed * (0.18 + _level * 0.45)) % 4;
        if (Intensity <= 0 || Math.Max(_level, _pulse) < 0.014 || (!_settings.EdgeGlow && _settings.Style == VisualStyle.None))
            return false;

        bool useScreen = screenColors != null && _settings.ColorSource != ColorSource.Palette;
        Color[] stops = _stops;
        if (useScreen)
        {
            double screenAmount = _settings.ColorSource == ColorSource.Screen ? 1 : _settings.ScreenBlend / 100.0;
            double follow = _hasScreenColors ? 1 - Math.Exp(-elapsed / 0.28) : 1;
            for (int i = 0; i < 4; i++)
                _blendedStops[i] = Mix(_blendedStops[i], Mix(_stops[i], screenColors![i], screenAmount), follow);
            stops = _blendedStops;
        }
        _hasScreenColors = useScreen;
        for (int i = 0; i < 4; i++)
        {
            double sample = ((_settings.AnimateColors ? _phase : 0) + i) % 4;
            int index = (int)sample;
            _colors[i] = Mix(stops[index], stops[(index + 1) % 4], sample - index);
        }
        return true;
    }

    public void Draw(DrawingContext drawing, double width, double height)
    {
        double shortSide = Math.Min(width, height);
        double depth = shortSide * Size * (0.045 + _drive * 0.067 + _pulse * 0.047);
        if (_settings.EdgeGlow)
            DrawGlow(drawing, width, height, depth);

        switch (_settings.Style)
        {
            case VisualStyle.EdgeBars:
                DrawSpectrum(drawing, width, height, depth, top: true, _colors[0]);
                DrawSpectrum(drawing, width, height, depth, top: false, _colors[2]);
                break;
            case VisualStyle.Orbit:
                DrawOrbit(drawing, new Point(width / 2, height / 2), shortSide);
                break;
            case VisualStyle.CenterBars:
                DrawCenterBars(drawing, width, height);
                break;
            case VisualStyle.Wave:
                DrawWave(drawing, width, height);
                break;
        }
    }

    private void DrawGlow(DrawingContext drawing, double width, double height, double depth)
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

        if (_beatAge < 0.45 && Math.Min(width, height) > 2 * (rail + depth))
        {
            double progress = Math.Clamp(_beatAge / 0.45, 0, 1);
            double inset = rail + depth * (0.1 + 0.72 * progress);
            double alpha = Intensity * _beatStrength * 0.58 * (1 - progress) * (1 - progress);
            var pen = new Pen(new SolidColorBrush(Alpha(Mix(_colors[1], _colors[3], 0.5), alpha)), 1.4);
            drawing.DrawRoundedRectangle(null, pen, new Rect(inset, inset, width - 2 * inset, height - 2 * inset), 17, 17);
        }
    }

    private static void DrawEdge(DrawingContext drawing, int edge, double width, double height,
        double depth, double rail, double strength, Color start, Color end, Color glow)
    {
        var (area, outer, inner, alongStart, alongEnd) = edge switch
        {
            0 => (new Rect(0, 0, width, depth), new Point(0, 0), new Point(0, 1), new Point(0, 0), new Point(1, 0)),
            1 => (new Rect(width - depth, 0, depth, height), new Point(1, 0), new Point(0, 0), new Point(0, 0), new Point(0, 1)),
            2 => (new Rect(0, height - depth, width, depth), new Point(0, 1), new Point(0, 0), new Point(1, 0), new Point(0, 0)),
            _ => (new Rect(0, 0, depth, height), new Point(0, 0), new Point(1, 0), new Point(0, 1), new Point(0, 0))
        };

        var vignette = new LinearGradientBrush { StartPoint = outer, EndPoint = inner };
        vignette.GradientStops.Add(new GradientStop(Alpha(glow, strength), 0));
        vignette.GradientStops.Add(new GradientStop(Alpha(glow, strength * 0.44), 0.22));
        vignette.GradientStops.Add(new GradientStop(Alpha(glow, strength * 0.12), 0.58));
        vignette.GradientStops.Add(new GradientStop(Alpha(glow, 0), 1));
        drawing.DrawRectangle(vignette, null, area);

        Rect strip = edge switch
        {
            0 => new Rect(0, 0, width, rail),
            1 => new Rect(width - rail, 0, rail, height),
            2 => new Rect(0, height - rail, width, rail),
            _ => new Rect(0, 0, rail, height)
        };
        double railAlpha = Math.Min(0.95, strength * 1.55);
        var highlight = new LinearGradientBrush { StartPoint = alongStart, EndPoint = alongEnd };
        highlight.GradientStops.Add(new GradientStop(Alpha(start, railAlpha), 0));
        highlight.GradientStops.Add(new GradientStop(Alpha(Mix(glow, Colors.White, 0.33), railAlpha * 0.9), 0.53));
        highlight.GradientStops.Add(new GradientStop(Alpha(end, railAlpha), 1));
        drawing.DrawRectangle(highlight, null, strip);
    }

    private void DrawSpectrum(DrawingContext drawing, double width, double height, double depth, bool top, Color color)
    {
        double span = width * (top ? 0.56 : 0.72);
        double step = span / _bands.Length;
        double barWidth = Math.Max(1, Math.Min(14, step * 0.42));
        double start = (width - span) / 2;
        double opacity = Intensity * (0.38 + _pulse * 0.40);
        var brush = new LinearGradientBrush(Alpha(color, opacity), Alpha(color, 0.10 * opacity),
            new Point(0, top ? 0 : 1), new Point(0, top ? 1 : 0));

        for (int i = 0; i < _bands.Length; i++)
        {
            double band = _bands[top ? _bands.Length - 1 - i : i];
            if (band < 0.035)
                continue;

            double barHeight = depth * (top ? 0.43 : 0.72) * band;
            double x = start + step * (i + 0.5) - barWidth / 2;
            double y = top ? 4 : height - 4 - barHeight;
            drawing.DrawRoundedRectangle(brush, null, new Rect(x, y, barWidth, barHeight), barWidth / 2, barWidth / 2);
        }
    }

    private void DrawOrbit(DrawingContext drawing, Point center, double shortSide)
    {
        double unit = shortSide * Size;
        double radius = unit * (0.16 + _drive * 0.042 + _pulse * 0.044);
        double opacity = Intensity * Math.Min(1, _level * 1.2 + _pulse * 0.6);
        double haloRadius = radius + unit * 0.07;
        double rim = radius / haloRadius;
        var halo = new RadialGradientBrush();
        halo.GradientStops.Add(new GradientStop(Alpha(_colors[2], 0), rim - 0.2));
        halo.GradientStops.Add(new GradientStop(Alpha(_colors[2], opacity * 0.43), rim));
        halo.GradientStops.Add(new GradientStop(Alpha(_colors[2], 0), 1));
        drawing.DrawEllipse(halo, null, center, haloRadius, haloRadius);
        drawing.DrawEllipse(null, new Pen(SpectrumBrush(opacity), Math.Clamp(unit * 0.003, 1, 5)), center, radius, radius);

        for (int i = 0; i < 4; i++)
        {
            var pen = new Pen(new SolidColorBrush(Alpha(_colors[i], opacity * 0.94)),
                Math.Clamp(unit * 0.0035, 1, 6)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            int first = i * _bands.Length / 4;
            int end = (i + 1) * _bands.Length / 4;
            for (int band = first; band < end; band++)
            {
                double angle = 2 * Math.PI * band / _bands.Length - Math.PI / 2 + _phase * 0.12;
                var direction = new Vector(Math.Cos(angle), Math.Sin(angle));
                double start = radius + unit * 0.013;
                double length = unit * (0.006 + _bands[band] * 0.10);
                drawing.DrawLine(pen, center + direction * start, center + direction * (start + length));
            }
        }
    }

    private void DrawCenterBars(DrawingContext drawing, double width, double height)
    {
        double span = width * 0.53 * Size;
        double step = span / _bands.Length;
        double barWidth = Math.Max(1, step * 0.53);
        var brush = new LinearGradientBrush(Alpha(_colors[0], Intensity * 0.85), Alpha(_colors[2], Intensity * 0.73),
            new Point(0, 0), new Point(0, 1));
        for (int i = 0; i < _bands.Length; i++)
        {
            double band = _bands[i];
            if (band < 0.03)
                continue;
            double barHeight = height * Size * band * (0.30 + _pulse * 0.12);
            double x = (width - span) / 2 + step * (i + 0.5) - barWidth / 2;
            drawing.DrawRoundedRectangle(brush, null, new Rect(x, (height - barHeight) / 2, barWidth, barHeight),
                barWidth / 2, barWidth / 2);
        }
    }

    private void DrawWave(DrawingContext drawing, double width, double height)
    {
        double span = width * Math.Min(0.93, 0.62 * Size);
        double start = (width - span) / 2;
        double step = span / (_bands.Length + 1);
        double center = height / 2;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(start, center), false, false);
            for (int i = 0; i <= _bands.Length; i++)
            {
                double y = i == _bands.Length ? center
                    : center + (i % 2 == 0 ? 1 : -1) * _bands[i] * height * Size * (0.13 + _pulse * 0.08);
                double x = start + (i + 1) * step;
                path.QuadraticBezierTo(new Point(x - step / 2, y), new Point(x, y), true, true);
            }
        }
        double opacity = Intensity * Math.Min(1, _level * 1.4 + _pulse * 0.55);
        double thickness = Math.Clamp(Math.Min(width, height) * 0.0035, 1.2, 7);
        drawing.DrawGeometry(null, new Pen(SpectrumBrush(opacity * 0.24), thickness * 3), geometry);
        drawing.DrawGeometry(null, new Pen(SpectrumBrush(opacity), thickness), geometry);
    }

    private LinearGradientBrush SpectrumBrush(double opacity)
        => new(Alpha(_colors[0], opacity), Alpha(_colors[2], opacity), new Point(0, 0), new Point(1, 1));

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
