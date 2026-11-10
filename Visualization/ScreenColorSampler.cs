using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Color = System.Windows.Media.Color;
using Screen = System.Windows.Forms.Screen;

namespace PartyTime.Visualization;

internal sealed partial class ScreenColorSampler : IDisposable
{
    private readonly Timer _timer;
    private Color[]? _latest;
    private int _sampling;

    public ScreenColorSampler()
        => _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));

    public Color[]? Latest => Volatile.Read(ref _latest);

    public void Dispose() => _timer.Dispose();

    private void Sample()
    {
        if (Interlocked.Exchange(ref _sampling, 1) != 0)
            return;

        try
        {
            if (Screen.PrimaryScreen is { } screen)
                Volatile.Write(ref _latest, ReadPalette(screen.Bounds));
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException)
        {
            Debug.WriteLine(ex);
            Volatile.Write(ref _latest, null);
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    internal static Color[]? ReadPalette(Rectangle bounds)
    {
        using var sample = new Bitmap(48, 28, PixelFormat.Format32bppRgb);
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

        const int bucketCount = 13;
        Span<Bucket> buckets = stackalloc Bucket[4 * bucketCount];
        buckets.Clear();
        for (int y = 0; y < sample.Height; y++)
        {
            for (int x = 0; x < sample.Width; x++)
            {
                var pixel = sample.GetPixel(x, y);
                int max = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
                int min = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                if (max < 24)
                    continue;
                double chroma = (max - min) / 255.0;
                int hue = chroma > 0.09 ? Math.Min(11, (int)(pixel.GetHue() / 30)) : 12;
                int area = y < sample.Height / 2 ? (x < sample.Width / 2 ? 0 : 1) : (x < sample.Width / 2 ? 3 : 2);
                double weight = (0.07 + chroma * 2.1) * max / 255;
                ref var bucket = ref buckets[area * bucketCount + hue];
                bucket.Weight += weight;
                bucket.Red += pixel.R * weight;
                bucket.Green += pixel.G * weight;
                bucket.Blue += pixel.B * weight;
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
