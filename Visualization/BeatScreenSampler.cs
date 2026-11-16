using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PartyTime.Settings;

namespace PartyTime.Visualization;

internal sealed class BeatScreenSampler : IDisposable
{
    private readonly Timer _timer;
    private readonly DesktopCapture _capture;
    private readonly int _width, _height;
    private readonly byte[] _current, _sample, _output;
    private readonly WriteableBitmap _bitmap;
    private ScreenSampleRequest? _request;
    private string? _error;
    private long _generation = -1;
    private int _state;
    private bool _hasFrame;
    public BeatScreenSampler(Screen screen, bool automaticCapture = true)
    {
        double scale = Math.Min(1, 960.0 / Math.Max(screen.Bounds.Width, screen.Bounds.Height));
        _width = Math.Max(1, (int)Math.Round(screen.Bounds.Width * scale));
        _height = Math.Max(1, (int)Math.Round(screen.Bounds.Height * scale));
        _current = new byte[_width * _height * 4];
        _sample = new byte[_current.Length];
        _output = new byte[_current.Length];
        _bitmap = new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        _capture = new DesktopCapture(screen, _width, _height);
        _timer = new Timer(_ => CaptureFrame(), null, automaticCapture ? TimeSpan.Zero : Timeout.InfiniteTimeSpan,
            automaticCapture ? TimeSpan.FromMilliseconds(33) : Timeout.InfiniteTimeSpan);
    }
    public Bitmap? Latest => Volatile.Read(ref _hasFrame) ? _bitmap : null;
    public string? Error => Volatile.Read(ref _error) ?? _capture.Error;
    public void Update(ScreenSampleRequest request) => Volatile.Write(ref _request, request);
    internal bool CaptureFrame()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return false;
        try
        {
            var request = Volatile.Read(ref _request);
            long now = Stopwatch.GetTimestamp();
            if (request == null || request.Opacity(now) < 0.005 || !_capture.ReadPixels(_current)) return false;
            if (_generation != request.Generation) { _current.CopyTo(_sample, 0); _generation = request.Generation; }
            BlendPixels(_current, _sample, _output, _width, _height, request.Settings.SampleBlend, 1 + request.Settings.SampleZoom / 100.0 * request.Pulse(now));
            using (var frame = _bitmap.Lock())
                for (int y = 0; y < _height; y++) Marshal.Copy(_output, y * _width * 4, frame.Address + y * frame.RowBytes, _width * 4);
            Volatile.Write(ref _hasFrame, true);
            Volatile.Write(ref _error, null);
            return true;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or System.IO.IOException or ArgumentException)
        {
            Volatile.Write(ref _error, ex.Message);
            return false;
        }
        finally { if (Interlocked.CompareExchange(ref _state, 0, 1) == 2) Release(); }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte BlendChannel(byte background, byte sample, ScreenSampleBlend blend) => blend switch
    {
        ScreenSampleBlend.Additive => (byte)Math.Min(255, background + sample),
        ScreenSampleBlend.Screen => (byte)(255 - (255 - background) * (255 - sample) / 255),
        ScreenSampleBlend.Multiply => (byte)(background * sample / 255),
        ScreenSampleBlend.Difference => (byte)Math.Abs(background - sample),
        ScreenSampleBlend.Negative => (byte)(255 - sample),
        _ => sample
    };

    internal static void BlendPixels(ReadOnlySpan<byte> background, ReadOnlySpan<byte> sample, Span<byte> output,
        int width, int height, ScreenSampleBlend blend, double zoom)
    {
        // The horizontal mapping is identical on every row; calculate it once per frame.
        Span<int> columns = stackalloc int[width];
        for (int x = 0; x < width; x++)
            columns[x] = Math.Clamp((int)((x - width / 2.0) / zoom + width / 2.0), 0, width - 1) * 4;
        for (int y = 0; y < height; y++)
        {
            int sy = Math.Clamp((int)((y - height / 2.0) / zoom + height / 2.0), 0, height - 1);
            int targetRow = y * width * 4, sourceRow = sy * width * 4;
            for (int x = 0; x < width; x++)
            {
                int target = targetRow + x * 4, source = sourceRow + columns[x];
                output[target] = BlendChannel(background[target], sample[source], blend);
                output[target + 1] = BlendChannel(background[target + 1], sample[source + 1], blend);
                output[target + 2] = BlendChannel(background[target + 2], sample[source + 2], blend);
                output[target + 3] = 255;
            }
        }
    }


    public void Dispose()
    {
        _timer.Dispose();
        Volatile.Write(ref _hasFrame, false);
        if (Interlocked.Exchange(ref _state, 2) == 0) Release();
    }
    private void Release()
    {
        _capture.Dispose();
        // Retained visuals may still reference the final bitmap. Avalonia releases it
        // when those visuals and the preview release their references.
    }
}
