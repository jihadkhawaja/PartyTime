using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using PartyTime.Audio;
using PartyTime.Settings;
using PartyTime.Visualization;

namespace PartyTime.Controls;

public sealed class StylePreview : Control
{
    private static readonly Brush ScreenSurface = SceneBrush.Linear(
        Color.FromRgb(20, 17, 36), Color.FromRgb(25, 31, 52), new Point(0, 1), new Point(1, 0));
    private static readonly Brush MockWindow = new SolidColorBrush(Color.FromRgb(39, 37, 63));
    private static readonly Brush MockContent = new SolidColorBrush(Color.FromRgb(58, 54, 82));

    private readonly Scene _scene = new();
    private readonly DispatcherTimer _timer;
    private readonly long _start = Stopwatch.GetTimestamp();
    private FrequencyRange _range = FrequencyRange.Full;
    private bool _showScene;

    public StylePreview()
    {
        ClipToBounds = true;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _timer.Tick += (_, _) =>
        {
            if ((TopLevel.GetTopLevel(this) as Window)?.WindowState != WindowState.Minimized)
                UpdatePreview();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (IsVisible)
                _timer.Start();
            else
                _timer.Stop();
        };
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    internal Func<Color[]?>? GetScreenColors { get; set; }

    internal void ApplySettings(VisualizerSettings settings)
    {
        _scene.ApplySettings(settings);
        _range = settings.GetRange();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        const double beatLength = 0.54;
        long now = Stopwatch.GetTimestamp();
        double seconds = Stopwatch.GetElapsedTime(_start, now).TotalSeconds;
        double beat = Math.Floor(seconds / beatLength) * beatLength;
        long beatTime = _start + (long)(beat * Stopwatch.Frequency);
        double hit = Math.Exp(-(seconds - beat) * 12);
        AudioSignal Sample(FrequencyRange range)
        {
            var bands = new float[AudioAnalyzer.BandCount];
            for (int i = 0; i < bands.Length; i++)
            {
                double movement = 0.5 + Math.Sin(seconds * 5.4 - i * 0.47) * 0.25 + Math.Sin(seconds * 9 + i * 1.61) * 0.17;
                bands[i] = AudioAnalyzer.IncludesBand(i, range) ? (float)Math.Min(1, movement + hit * 0.28) : 0;
            }
            return new AudioSignal((float)(0.52 + hit * 0.21), (float)(0.24 + hit * 0.62),
                bands, beatTime, 0.72f, range);
        }
        var frame = new AudioFrame(now, Sample(_range), Sample(FrequencyRange.Full),
            Sample(FrequencyRange.Bass), Sample(FrequencyRange.Mids), Sample(FrequencyRange.Treble));
        _scene.UpdatePointer(Bounds.Width > 0 && Bounds.Height > 0
            ? new Point(Bounds.Width * (0.5 + Math.Sin(seconds * 1.7) * 0.32), Bounds.Height * (0.5 + Math.Cos(seconds * 2.3) * 0.28))
            : null, now);
        _showScene = _scene.Update(frame, now, _timer.Interval.TotalSeconds, GetScreenColors?.Invoke());
        InvalidateVisual();
    }

    public override void Render(DrawingContext drawing)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        drawing.DrawRectangle(ScreenSurface, null, new Rect(0, 0, width, height), 7, 7);
        double mockWidth = width * 0.4;
        double x = (width - mockWidth) / 2;
        double y = height * 0.19;
        drawing.DrawRectangle(MockWindow, null, new Rect(x, y, mockWidth, height * 0.62), 6, 6);
        drawing.DrawRectangle(MockContent, null, new Rect(x + 10, y + 9, 20, 2), 1, 1);
        drawing.DrawRectangle(MockContent, null, new Rect(x + 10, y + 17, Math.Max(1, mockWidth - 20), height * 0.30), 4, 4);
        if (_showScene)
            _scene.Draw(drawing, width, height);
    }
}
