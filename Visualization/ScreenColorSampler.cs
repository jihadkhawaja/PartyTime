using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Media;
using Avalonia.Platform;

namespace PartyTime.Visualization;

internal sealed class ScreenColorSampler : IDisposable
{
    internal const int SampleWidth = 48, SampleHeight = 28;
    private readonly Timer _timer;
    private readonly DesktopCapture _capture;
    private readonly byte[] _pixels = new byte[SampleWidth * SampleHeight * 4];
    private Color[]? _latest;
    private int _state;
    public ScreenColorSampler(Screen screen)
    {
        _capture = new DesktopCapture(screen, SampleWidth, SampleHeight, frameRate: 4);
        _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
    }
    public Color[]? Latest => Volatile.Read(ref _latest);
    private void Sample()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
        try { if (_capture.ReadPixels(_pixels)) Volatile.Write(ref _latest, ExtractPalette(_pixels, SampleWidth * 4)); }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or System.IO.IOException) { Volatile.Write(ref _latest, null); }
        finally { if (Interlocked.CompareExchange(ref _state, 0, 1) == 2) _capture.Dispose(); }
    }
    public void Dispose() { _timer.Dispose(); if (Interlocked.Exchange(ref _state, 2) == 0) _capture.Dispose(); }
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

}
