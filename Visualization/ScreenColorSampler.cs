using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using SharpGen.Runtime;
using Color = System.Windows.Media.Color;

namespace PartyTime.Visualization;

internal sealed partial class ScreenColorSampler : IDisposable
{
    internal const int SampleWidth = 48;
    internal const int SampleHeight = 28;
    private const int Idle = 0, Sampling = 1, Disposed = 2;

    private readonly Timer _timer;
    private readonly Rectangle _bounds;
    private GpuScreenCapture? _gpu;
    private Color[]? _latest;
    private long _lastGpuFailure;
    private bool _tryGpu = true;
    private int _state;

    public ScreenColorSampler(Rectangle bounds)
    {
        _bounds = bounds;
        _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
    }

    public Color[]? Latest => Volatile.Read(ref _latest);

    public void Dispose()
    {
        _timer.Dispose();
        if (Interlocked.Exchange(ref _state, Disposed) == Idle)
            _gpu?.Dispose();
    }

    private void Sample()
    {
        if (Interlocked.CompareExchange(ref _state, Sampling, Idle) != Idle)
            return;

        try
        {
            try
            {
                if (_gpu == null && _tryGpu
                    && (_lastGpuFailure == 0 || Stopwatch.GetElapsedTime(_lastGpuFailure).TotalSeconds >= 5))
                {
                    _gpu = GpuScreenCapture.Open(_bounds);
                    _tryGpu = _gpu != null;
                }

                if (_gpu != null)
                {
                    if (_gpu.TryReadPalette(out var palette))
                        Volatile.Write(ref _latest, palette);
                    return;
                }
            }
            catch (SharpGenException ex)
            {
                Debug.WriteLine(ex);
                _gpu?.Dispose();
                _gpu = null;
                _lastGpuFailure = Stopwatch.GetTimestamp();
            }
            Volatile.Write(ref _latest, ReadPalette(_bounds));
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException)
        {
            Debug.WriteLine(ex);
            Volatile.Write(ref _latest, null);
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _state, Idle, Sampling) == Disposed)
                _gpu?.Dispose();
        }
    }

    private static unsafe Color[]? ReadPalette(Rectangle bounds)
    {
        using var sample = new Bitmap(SampleWidth, SampleHeight, PixelFormat.Format32bppRgb);
        using var graphics = Graphics.FromImage(sample);
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
            return null;

        try
        {
            IntPtr destination = graphics.GetHdc();
            try
            {
                const uint sourceCopy = 0x00CC0020;
                if (!StretchBlt(destination, 0, 0, sample.Width, sample.Height, screen,
                    bounds.X, bounds.Y, bounds.Width, bounds.Height, sourceCopy))
                    return null;
            }
            finally { graphics.ReleaseHdc(destination); }
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }

        var data = sample.LockBits(new Rectangle(0, 0, SampleWidth, SampleHeight), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            int stride = data.Stride;
            var start = (byte*)data.Scan0 + Math.Min(0, stride) * (SampleHeight - 1);
            return ExtractPalette(new ReadOnlySpan<byte>(start, Math.Abs(stride) * SampleHeight), stride);
        }
        finally
        {
            sample.UnlockBits(data);
        }
    }

    internal static Color[]? ExtractPalette(ReadOnlySpan<byte> pixels, int stride)
    {
        const int bucketCount = 13;
        Span<Bucket> buckets = stackalloc Bucket[4 * bucketCount];
        buckets.Clear();
        int firstRow = -Math.Min(0, stride) * (SampleHeight - 1);
        for (int y = 0; y < SampleHeight; y++)
        {
            int row = firstRow + y * stride;
            for (int x = 0; x < SampleWidth; x++)
            {
                int offset = row + x * 4;
                byte blue = pixels[offset], green = pixels[offset + 1], red = pixels[offset + 2];
                int max = Math.Max(red, Math.Max(green, blue));
                int min = Math.Min(red, Math.Min(green, blue));
                if (max < 24)
                    continue;
                double chroma = (max - min) / 255.0;
                int hue = chroma > 0.09 ? Math.Min(11, (int)(System.Drawing.Color.FromArgb(red, green, blue).GetHue() / 30)) : 12;
                int area = y < SampleHeight / 2 ? (x < SampleWidth / 2 ? 0 : 1) : (x < SampleWidth / 2 ? 3 : 2);
                double weight = (0.07 + chroma * 2.1) * max / 255;
                ref var bucket = ref buckets[area * bucketCount + hue];
                bucket.Weight += weight;
                bucket.Red += red * weight;
                bucket.Green += green * weight;
                bucket.Blue += blue * weight;
            }
        }

        var palette = new Color[4];
        Color? fallback = null;
        for (int i = 0; i < palette.Length; i++)
        {
            Bucket strongest = default;
            foreach (var bucket in buckets.Slice(i * bucketCount, bucketCount))
                if (bucket.Weight > strongest.Weight)
                    strongest = bucket;
            if (strongest.Weight <= 0)
                continue;

            double scale = Math.Max(1 / strongest.Weight, 145 / Math.Max(strongest.Red, Math.Max(strongest.Green, strongest.Blue)));
            palette[i] = Color.FromRgb((byte)Math.Round(strongest.Red * scale),
                (byte)Math.Round(strongest.Green * scale), (byte)Math.Round(strongest.Blue * scale));
            fallback ??= palette[i];
        }
        if (fallback is null)
            return null;

        for (int i = 0; i < palette.Length; i++)
            if (palette[i].A == 0)
                palette[i] = fallback.Value;
        return palette;
    }

    private struct Bucket { public double Weight, Red, Green, Blue; }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr window);
    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr window, IntPtr dc);
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StretchBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
}
