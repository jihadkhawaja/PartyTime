using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Windows;
using PartyTime.Audio;
using PartyTime.Settings;
using PartyTime.Visualization;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Point = System.Windows.Point;

internal static partial class Program
{
    private static void RunCursorBenchmark()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null,
            out ID3D11Device? device, out Vortice.Direct3D.FeatureLevel _).CheckError();
        using var gpu = device!;
        using var dxgi = gpu.QueryInterface<IDXGIDevice>();
        using var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        using var d2d = factory.CreateDevice(dxgi);
        using var context = d2d.CreateDeviceContext(DeviceContextOptions.None);
        using var drawing = new GpuSceneDrawing(factory, context);
        var format = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        using var target = context.CreateBitmap(new SizeI(1920, 1080), IntPtr.Zero, 0,
            new BitmapProperties1(format, 96, 96, BitmapOptions.Target));
        context.Target = target;
        foreach (var style in Enum.GetValues<CursorTrailStyle>())
        {
            var scene = new Scene();
            scene.ApplySettings(Quiet with { CursorTrailEnabled = true, CursorTrail = style, CursorTrailSize = 40,
                CursorTrailLength = 2000, CursorShape = ShapeKind.Star, AnimateColors = false });
            long start = Stopwatch.GetTimestamp();
            const int warmup = 60, frames = 180;
            var times = new double[frames];
            long allocated = 0;
            for (int frame = 0; frame < warmup + frames; frame++)
            {
                if (frame == warmup)
                    allocated = GC.GetAllocatedBytesForCurrentThread();
                long before = Stopwatch.GetTimestamp();
                long now = start + (long)(frame / 120.0 * Stopwatch.Frequency);
                double t = frame / 120.0;
                scene.UpdatePointer(new Point(960 + Math.Sin(t * 6) * 800, 540 + Math.Cos(t * 4) * 300), now);
                scene.Update(AudioFrame.Silent, now, 1.0 / 120, null);
                context.BeginDraw();
                context.Clear(new Color4(0, 0, 0, 0));
                scene.Draw(drawing, 1920, 1080);
                context.EndDraw().CheckError();
                if (frame >= warmup)
                    times[frame - warmup] = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Array.Sort(times);
            Console.WriteLine(JsonSerializer.Serialize(new { Style = style.ToString(), Frames = frames,
                AverageMs = Math.Round(times.Average(), 3), P95Ms = Math.Round(times[(int)(frames * 0.95)], 3),
                AllocatedBytesPerFrame = bytes / frames }));
        }
        context.Target = null;
    }
}
