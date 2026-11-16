using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PartyTime.Visualization;

internal static class BitmapPixels
{
    public static Bitmap Create(byte[] pixels, int width, int height)
    {
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, pin.AddrOfPinnedObject(), new PixelSize(width, height), new Vector(96, 96), width * 4); }
        finally { pin.Free(); }
    }

    public static byte[] Read(Bitmap bitmap)
    {
        using var target = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var framebuffer = target.Lock();
        bitmap.CopyPixels(framebuffer);
        var pixels = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
        for (int y = 0; y < bitmap.PixelSize.Height; y++)
            Marshal.Copy(framebuffer.Address + y * framebuffer.RowBytes, pixels, y * bitmap.PixelSize.Width * 4, bitmap.PixelSize.Width * 4);
        return pixels;
    }
}
