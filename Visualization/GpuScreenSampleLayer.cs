using System;
using System.Windows.Media.Imaging;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace PartyTime.Visualization;

// A reusable upload per display keeps changing captures out of the imported-image cache.
internal sealed class GpuScreenSampleLayer(ID2D1DeviceContext context) : IDisposable
{
    private ID2D1Bitmap? _bitmap;
    private BitmapSource? _source;
    private byte[] _pixels = [];

    public unsafe void Draw(BitmapSource? image, double opacity, double width, double height)
    {
        if (image == null || opacity <= 0)
        {
            Dispose();
            return;
        }
        int stride = image.PixelWidth * 4;
        if (!ReferenceEquals(image, _source))
        {
            if (_source?.PixelWidth != image.PixelWidth || _source?.PixelHeight != image.PixelHeight)
            {
                Dispose();
                _pixels = new byte[stride * image.PixelHeight];
                _bitmap = context.CreateBitmap(new SizeI(image.PixelWidth, image.PixelHeight), IntPtr.Zero, 0,
                    new BitmapProperties(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm,
                        Vortice.DCommon.AlphaMode.Premultiplied), 96, 96));
            }
            image.CopyPixels(_pixels, stride, 0);
            fixed (byte* pixels = _pixels)
                _bitmap!.CopyFromMemory((IntPtr)pixels, (uint)stride);
            _source = image;
        }
        context.DrawBitmap(_bitmap!, new RawRectF(0, 0, (float)width, (float)height), (float)opacity, InterpolationMode.Linear, null, null);
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _source = null;
        _pixels = [];
    }
}
