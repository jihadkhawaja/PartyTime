using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PartyTime.Audio;
using PartyTime.Settings;
using PartyTime.Visualization;

namespace PartyTime.Controls;

public sealed class StylePreview : FrameworkElement
{
    private static readonly Brush ScreenSurface = new LinearGradientBrush(
        Color.FromRgb(20, 17, 36), Color.FromRgb(25, 31, 52), new Point(0, 1), new Point(1, 0));
    private static readonly Brush MockWindow = new SolidColorBrush(Color.FromRgb(39, 37, 63));
    private static readonly Brush MockContent = new SolidColorBrush(Color.FromRgb(58, 54, 82));

    private readonly Scene _scene = new();
    private readonly float[] _bands = new float[AudioAnalyzer.BandCount];
    private readonly DispatcherTimer _timer;
    private readonly long _start = Stopwatch.GetTimestamp();
    private FrequencyRange _range = FrequencyRange.Full;
    private bool _showScene;

    public StylePreview()
    {
        ClipToBounds = true;
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _timer.Tick += (_, _) =>
        {
            if (Window.GetWindow(this)?.WindowState != WindowState.Minimized)
                UpdatePreview();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                _timer.Start();
            else
                _timer.Stop();
        };
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
        for (int i = 0; i < _bands.Length; i++)
        {
            double movement = 0.5 + Math.Sin(seconds * 5.4 - i * 0.47) * 0.25 + Math.Sin(seconds * 9 + i * 1.61) * 0.17;
            _bands[i] = AudioAnalyzer.IncludesBand(i, _range) ? (float)Math.Min(1, movement + hit * 0.28) : 0;
        }
        var frame = new AudioFrame((float)(0.52 + hit * 0.21), (float)(0.24 + hit * 0.62),
            _bands, now, beatTime, 0.72f, _range);
        _showScene = _scene.Update(frame, now, _timer.Interval.TotalSeconds, GetScreenColors?.Invoke());
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        drawing.DrawRoundedRectangle(ScreenSurface, null, new Rect(0, 0, width, height), 7, 7);
        double mockWidth = width * 0.4;
        double x = (width - mockWidth) / 2;
        double y = height * 0.19;
        drawing.DrawRoundedRectangle(MockWindow, null, new Rect(x, y, mockWidth, height * 0.62), 6, 6);
        drawing.DrawRoundedRectangle(MockContent, null, new Rect(x + 10, y + 9, 20, 2), 1, 1);
        drawing.DrawRoundedRectangle(MockContent, null, new Rect(x + 10, y + 17, Math.Max(1, mockWidth - 20), height * 0.30), 4, 4);
        if (_showScene)
            _scene.Draw(drawing, width, height);
    }
}
