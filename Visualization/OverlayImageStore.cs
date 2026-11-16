using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using BitmapSource = Avalonia.Media.Imaging.Bitmap;
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
        bitmap.Save(destination, new PngBitmapEncoderOptions());
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
        int stride = original.PixelSize.Width * 4;
        var pixels = BitmapPixels.Read(original);
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
        var bitmap = BitmapPixels.Create(pixels, original.PixelSize.Width, original.PixelSize.Height);
        return bitmap;
    }

    public static Color ReadPixel(BitmapSource original, int x, int y)
    {
        var pixels = BitmapPixels.Read(original);
        int i = (y * original.PixelSize.Width + x) * 4;
        return Color.FromArgb(pixels[i + 3], Unpremultiply(pixels[i + 2], pixels[i + 3]),
            Unpremultiply(pixels[i + 1], pixels[i + 3]), Unpremultiply(pixels[i], pixels[i + 3]));
    }

    private static byte Unpremultiply(byte channel, byte alpha)
        => alpha == 0 ? (byte)0 : (byte)Math.Min(255, (channel * 255 + alpha / 2) / alpha);

    public static bool IsImageError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or FormatException or NotSupportedException or ArgumentException or InvalidOperationException or System.Security.SecurityException;

    private static BitmapSource Decode(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length >= 20 * 1024 * 1024)
            throw new InvalidOperationException("Choose an image smaller than 20 MB.");
        using var codec = SkiaSharp.SKCodec.Create(input) ?? throw new FormatException("This image cannot be decoded.");
        int width = codec.Info.Width, height = codec.Info.Height;
        if (width > 16_384 || height > 16_384 || width <= 0 || height <= 0)
            throw new InvalidOperationException("Choose an image no larger than 16,384 pixels on either side.");
        using var source = File.OpenRead(path);
        using var decoded = width >= height ? Bitmap.DecodeToWidth(source, Math.Min(1024, width))
            : Bitmap.DecodeToHeight(source, Math.Min(1024, height));
        return BitmapPixels.Create(BitmapPixels.Read(decoded), decoded.PixelSize.Width, decoded.PixelSize.Height);
    }
}
