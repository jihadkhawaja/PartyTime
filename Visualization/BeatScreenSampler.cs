using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PartyTime.Settings;
using SharpGen.Runtime;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace PartyTime.Visualization;

// Capture and blend off the UI thread. Samples stay in memory and are discarded on stop.
internal sealed partial class BeatScreenSampler : IDisposable
{
    private readonly Rectangle _bounds;
    private readonly Timer _timer;
    private readonly int _width, _height;
    private readonly byte[] _current, _sample, _output;
    private ScreenSampleRequest? _request;
    private BitmapSource? _latest;
    private string? _error;
    private Bitmap? _capture;
    private Graphics? _graphics;
    private GpuScreenCapture? _gpu;
    private bool _hasDesktop;
    private bool _tryGpu = true;
    private long _lastGpuFailure;
    private string _backend = "Waiting for capture";
    private long _generation = -1;
    private long _retryAfter;
    private int _state; // 0 idle, 1 capturing, 2 disposed

    public BeatScreenSampler(Rectangle bounds, bool automaticCapture = true)
    {
        _bounds = bounds;
        double scale = Math.Min(1, 960.0 / Math.Max(bounds.Width, bounds.Height));
        _width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        _height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
        _current = new byte[_width * _height * 4];
        _sample = new byte[_current.Length];
        _output = new byte[_current.Length];
        _timer = new Timer(_ => CaptureFrame(), null,
            automaticCapture ? TimeSpan.Zero : Timeout.InfiniteTimeSpan,
            automaticCapture ? TimeSpan.FromMilliseconds(33) : Timeout.InfiniteTimeSpan);
    }

    public BitmapSource? Latest => Volatile.Read(ref _latest);
    public string? Error => Volatile.Read(ref _error);
    public string CaptureBackend => Volatile.Read(ref _backend);
    public void Update(ScreenSampleRequest request) => Volatile.Write(ref _request, request);

    // Manual scheduling lets the benchmark measure this exact pipeline at a fixed cadence.
    internal bool CaptureFrame()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return false;
        try
        {
            var request = Volatile.Read(ref _request);
            long now = Stopwatch.GetTimestamp();
            if (request == null || request.Opacity(now) < 0.005 || now < _retryAfter) return false;
            if (!ReadDesktop(now)) return false;

            if (_generation != request.Generation)
            {
                _current.CopyTo(_sample, 0);
                _generation = request.Generation;
            }
            double zoom = 1 + request.Settings.SampleZoom / 100.0 * request.Pulse(now);
            BlendPixels(_current, _sample, _output, _width, _height, request.Settings.SampleBlend, zoom);
            var bitmap = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Pbgra32, null, _output, _width * 4);
            bitmap.Freeze();
            Volatile.Write(ref _latest, bitmap);
            Volatile.Write(ref _error, null);
            return true;
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException)
        {
            Debug.WriteLine(ex);
            Volatile.Write(ref _latest, null);
            Volatile.Write(ref _error, "Screen capture is unavailable. PartyTime will retry automatically.");
            _retryAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            return false;
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _state, 0, 1) == 2) ReleaseCapture();
        }
    }

    private bool ReadDesktop(long now)
    {
        try
        {
            if (_gpu == null && _tryGpu
                && (_lastGpuFailure == 0 || Stopwatch.GetElapsedTime(_lastGpuFailure, now).TotalSeconds >= 5))
            {
                _gpu = GpuScreenCapture.Open(_bounds, _width, _height);
                _tryGpu = _gpu != null;
            }
            if (_gpu != null)
            {
                _gpu.TryReadPixels(_current);
                if (_gpu.HasFrame || _hasDesktop)
                {
                    _hasDesktop = true;
                    Volatile.Write(ref _backend, "GPU desktop duplication");
                    return true;
                }
            }
        }
        catch (SharpGenException ex)
        {
            Debug.WriteLine(ex);
            _gpu?.Dispose();
            _gpu = null;
            _lastGpuFailure = now;
        }
        Volatile.Write(ref _backend, "GDI fallback");
        ReadDesktopGdi();
        _hasDesktop = true;
        return true;
    }

    private void ReadDesktopGdi()
    {
        _capture ??= new Bitmap(_width, _height, PixelFormat.Format32bppRgb);
        _graphics ??= Graphics.FromImage(_capture);
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new InvalidOperationException("Desktop capture is unavailable.");
        try
        {
            IntPtr destination = _graphics.GetHdc();
            try
            {
                if (!StretchBlt(destination, 0, 0, _width, _height, screen,
                    _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0x00CC0020))
                    throw new InvalidOperationException("Desktop capture is unavailable.");
            }
            finally { _graphics.ReleaseHdc(destination); }
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
        var data = _capture.LockBits(new Rectangle(0, 0, _width, _height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < _height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, _current, y * _width * 4, _width * 4);
        }
        finally { _capture.UnlockBits(data); }
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
        Volatile.Write(ref _latest, null);
        if (Interlocked.Exchange(ref _state, 2) == 0) ReleaseCapture();
    }

    private void ReleaseCapture()
    {
        _graphics?.Dispose();
        _capture?.Dispose();
        _gpu?.Dispose();
        _graphics = null;
        _capture = null;
        _gpu = null;
        Volatile.Write(ref _latest, null);
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr window);
    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr window, IntPtr dc);
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StretchBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
}
