using System;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace PartyTime.Visualization;

// Keep music effects at their chosen cadence while the cursor is composited independently.
internal sealed class GpuSceneLayer(ID2D1DeviceContext context, GpuSceneDrawing drawing, int pixelWidth, int pixelHeight) : IDisposable
{
    private ID2D1Bitmap1? _bitmap;
    public bool Visible { get; private set; }
    public int RenderCount { get; private set; }

    public void Update(Scene? scene, bool redraw, double width, double height)
    {
        if (!redraw)
            return;
        bool visible = scene?.BaseVisible == true;
        if (!visible && !Visible)
            return;
        _bitmap ??= context.CreateBitmap(new SizeI(pixelWidth, pixelHeight), IntPtr.Zero, 0, new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96, 96, BitmapOptions.Target));
        context.Target = _bitmap;
        context.BeginDraw();
        try
        {
            context.Clear(new Color4(0, 0, 0, 0));
            if (visible)
                scene!.DrawBase(drawing, width, height);
        }
        finally
        {
            var completed = context.EndDraw();
            context.Target = null;
            completed.CheckError();
        }
        Visible = visible;
        RenderCount++;
    }

    public void Draw(double width, double height)
    {
        if (Visible)
            context.DrawBitmap(_bitmap!, new RawRectF(0, 0, (float)width, (float)height), 1, InterpolationMode.Linear, null, null);
    }

    public void Clear()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        Visible = false;
    }

    public void Dispose() => Clear();
}
