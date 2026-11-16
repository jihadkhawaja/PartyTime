using System.Collections.Generic;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace PartyTime.Visualization;

// All displays and the preview share one raster image/texture identity per imported bitmap.
internal static class SkiaImageCache
{
    private sealed class Entry(SKImage image) { public SKImage Image { get; } = image; public int Users = 1; }
    private static readonly object Gate = new();
    private static readonly Dictionary<Bitmap, Entry> Images = new();

    public static SKImage Acquire(Bitmap bitmap)
    {
        lock (Gate)
        {
            if (Images.TryGetValue(bitmap, out var cached)) { cached.Users++; return cached.Image; }
            using var pixels = SKData.CreateCopy(BitmapPixels.Read(bitmap));
            var image = SKImage.FromPixels(new SKImageInfo(bitmap.PixelSize.Width, bitmap.PixelSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul), pixels, bitmap.PixelSize.Width * 4);
            Images.Add(bitmap, new Entry(image));
            return image;
        }
    }

    public static void Release(Bitmap bitmap)
    {
        lock (Gate)
        {
            if (!Images.TryGetValue(bitmap, out var entry) || --entry.Users > 0) return;
            Images.Remove(bitmap);
            entry.Image.Dispose();
        }
    }
}
