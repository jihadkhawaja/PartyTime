using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PartyTime.Settings;

namespace PartyTime.Visualization;

internal static class OverlayImageStore
{
    private static readonly Dictionary<string, WeakReference<BitmapSource>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(string Path, string Color, int Tolerance), WeakReference<BitmapSource>> KeyedCache = [];
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PartyTime", "Images");

    public static string Import(string path)
    {
        var bitmap = Decode(path);
        Directory.CreateDirectory(DirectoryPath);
        string destination = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(destination))
            encoder.Save(output);
        Cache[destination] = new(bitmap);
        return destination;
    }

    public static BitmapSource? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (Cache.TryGetValue(path, out var reference) && reference.TryGetTarget(out var cached))
            return cached;
        try
        {
            var bitmap = Decode(path);
            Cache[path] = new(bitmap);
            return bitmap;
        }
        catch (Exception ex) when (IsImageError(ex))
        {
            return null;
        }
    }

    public static BitmapSource? Load(OverlayImageSettings settings)
    {
        var original = Load(settings.Path);
        if (original == null || !settings.ChromaKeyEnabled)
            return original;
        VisualizerSettings.TryReadColor(settings.ChromaKeyColor, out var color);
        var key = (settings.Path, VisualizerSettings.ColorHex(color), settings.ChromaKeyTolerance);
        if (KeyedCache.TryGetValue(key, out var reference) && reference.TryGetTarget(out var cached))
            return cached;
        var keyed = RemoveColor(original, color, settings.ChromaKeyTolerance);
        if (KeyedCache.Count >= 128)
            foreach (var unused in KeyedCache.Where(entry => !entry.Value.TryGetTarget(out _)).Select(entry => entry.Key).ToArray())
                KeyedCache.Remove(unused);
        KeyedCache[key] = new(keyed);
        return keyed;
    }

    private static BitmapSource RemoveColor(BitmapSource original, Color key, int tolerance)
    {
        int stride = original.PixelWidth * 4;
        var pixels = new byte[stride * original.PixelHeight];
        original.CopyPixels(pixels, stride, 0);
        double threshold = Math.Clamp(tolerance, 0, 100) / 100.0 * Math.Sqrt(3 * 255 * 255);
        double feather = Math.Min(24, threshold * 0.25);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte alpha = pixels[i + 3];
            if (alpha == 0)
                continue;
            double red = Unpremultiply(pixels[i + 2], alpha) - key.R;
            double green = Unpremultiply(pixels[i + 1], alpha) - key.G;
            double blue = Unpremultiply(pixels[i], alpha) - key.B;
            double distance = Math.Sqrt(red * red + green * green + blue * blue);
            double keep = distance <= threshold ? 0 : feather > 0 ? Math.Clamp((distance - threshold) / feather, 0, 1) : 1;
            if (keep >= 1)
                continue;
            for (int channel = 0; channel < 4; channel++)
                pixels[i + channel] = (byte)Math.Round(pixels[i + channel] * keep);
        }
        var bitmap = BitmapSource.Create(original.PixelWidth, original.PixelHeight, 96, 96,
            PixelFormats.Pbgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static Color ReadPixel(BitmapSource original, int x, int y)
    {
        var pixel = new byte[4];
        original.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], Unpremultiply(pixel[2], pixel[3]),
            Unpremultiply(pixel[1], pixel[3]), Unpremultiply(pixel[0], pixel[3]));
    }

    private static byte Unpremultiply(byte channel, byte alpha)
        => alpha == 0 ? (byte)0 : (byte)Math.Min(255, (channel * 255 + alpha / 2) / alpha);

    public static bool IsImageError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or FileFormatException or NotSupportedException or ArgumentException or InvalidOperationException or System.Security.SecurityException;

    private static BitmapSource Decode(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length >= 20 * 1024 * 1024)
            throw new InvalidOperationException("Choose an image smaller than 20 MB.");
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth > 16_384 || frame.PixelHeight > 16_384)
            throw new InvalidOperationException("Choose an image no larger than 16,384 pixels on either side.");
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        input.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height)
            bitmap.DecodePixelWidth = Math.Min(1024, width);
        else
            bitmap.DecodePixelHeight = Math.Min(1024, height);
        bitmap.StreamSource = input;
        bitmap.EndInit();
        // Both renderers use premultiplied BGRA so transparent edges blend identically.
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        converted.Freeze();
        return converted;
    }
}
