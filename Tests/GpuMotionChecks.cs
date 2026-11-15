using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using PartyTime.Settings;
using PartyTime.Visualization;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Colors = System.Windows.Media.Colors;

internal static partial class Program
{
    private static unsafe void CheckGpuMotion()
    {
        foreach (var driver in new[] { DriverType.Warp, DriverType.Hardware })
        {
            var created = D3D11.D3D11CreateDevice(null, driver, DeviceCreationFlags.BgraSupport, null,
                out ID3D11Device? device, out Vortice.Direct3D.FeatureLevel _);
            if (created.Failure && driver == DriverType.Hardware)
            {
                Console.WriteLine("SKIP Hardware rendering unavailable; WARP coverage completed.");
                continue;
            }
            created.CheckError();
            using var gpu = device!;
            using var dxgi = gpu.QueryInterface<IDXGIDevice>();
            using var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
            using var d2d = factory.CreateDevice(dxgi);
            using var context = d2d.CreateDeviceContext(DeviceContextOptions.None);
            using var drawing = new GpuSceneDrawing(factory, context);
            var format = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
            using var target = context.CreateBitmap(new SizeI(800, 600), IntPtr.Zero, 0,
                new BitmapProperties1(format, 96, 96, BitmapOptions.Target));
            using var readback = context.CreateBitmap(new SizeI(800, 600), IntPtr.Zero, 0,
                new BitmapProperties1(format, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));

            int Render(Scene? scene, GpuSceneLayer? cached = null, double width = 800, double height = 600)
            {
                context.Target = target;
                context.BeginDraw();
                try
                {
                    context.Clear(new Color4(0, 0, 0, 0));
                    if (cached != null)
                    {
                        cached.Draw(width, height);
                        scene?.DrawCursor(drawing, width, height);
                    }
                    else
                        scene?.Draw(drawing, width, height);
                }
                finally
                {
                    var completed = context.EndDraw();
                    context.Target = null;
                    completed.CheckError();
                }
                readback.CopyFromBitmap(target);
                var mapped = readback.Map(MapOptions.Read);
                int visible = 0;
                try
                {
                    var bytes = (byte*)mapped.Bits;
                    for (int y = 0; y < 600; y++)
                        for (int x = 0; x < 800; x++)
                            if (bytes[y * mapped.Pitch + x * 4 + 3] > 0)
                                visible++;
                }
                finally { readback.Unmap(); }
                return visible;
            }

            foreach (var filled in new[] { false, true })
                foreach (var shape in Enum.GetValues<ShapeKind>())
                {
                    var scene = new Scene();
                    scene.ApplySettings(Quiet with { ShapesEnabled = true, Shape = shape, FilledShapes = filled, ShapeCount = 24 });
                    long now = Stopwatch.GetTimestamp();
                    scene.Update(MotionFrame(now), now, 0.1, null);
                    Check(Render(scene) > 0, $"{driver} draws {(filled ? "filled" : "outlined")} {shape} shapes");
                }

            foreach (var style in Enum.GetValues<CursorTrailStyle>())
                foreach (var shape in Enum.GetValues<ShapeKind>())
                {
                    var scene = new Scene();
                    scene.ApplySettings(Quiet with
                    {
                        ShapesEnabled = true, Shape = ShapeKind.Mixed, FilledShapes = false,
                        CursorTrailEnabled = true, CursorTrail = style, CursorShape = shape
                    });
                    long now = Stopwatch.GetTimestamp();
                    Point last = default;
                    for (int point = 0; point < 80; point++)
                    {
                        last = new Point(50 + point * 8, 300 + Math.Sin(point * 0.1) * 80);
                        scene.UpdatePointer(last, now - (79 - point) * Stopwatch.Frequency / 300);
                    }
                    scene.Update(MotionFrame(now), now, 0.1, null);
                    Check(Render(scene) > 0, $"{driver} renders {style}/{shape} cursor with outlined mixed shapes");
                    foreach (double seconds in new[] { 0.3, 0.64, 0.8 })
                    {
                        long later = now + (long)(seconds * Stopwatch.Frequency);
                        scene.UpdatePointer(last, later);
                        bool visible = scene.Update(PartyTime.Audio.AudioFrame.Silent, later, 0.2, null);
                        Render(visible ? scene : null);
                    }
                }
            Check(true, $"{driver} cursor and shape animations survive fading across frames");
            foreach (double scale in new[] { 1.0, 1.5, 2.0 })
            {
                context.SetDpi((float)(96 * scale), (float)(96 * scale));
                double width = 800 / scale, height = 600 / scale;
                using var layer = new GpuSceneLayer(context, drawing, 800, 600);
                var scene = new Scene();
                var settings = Quiet with { Border = BorderStyle.Glow };
                scene.ApplySettings(settings);
                long now = Stopwatch.GetTimestamp();
                scene.Update(MotionFrame(now), now, 0.1, null);
                int direct = Render(scene, width: width, height: height);
                layer.Update(scene, true, width, height);
                int composited = Render(null, layer, width, height);
                Check(composited > 0 && Math.Abs(direct - composited) <= Math.Max(10, direct / 100),
                    $"{driver} cached music layer preserves pixel coverage at {scale * 100}% DPI");
                scene.ApplySettings(settings with { CursorTrailEnabled = true });
                for (int frame = 0; frame < 12; frame++)
                {
                    long tick = now + frame * Stopwatch.Frequency / 120;
                    scene.UpdatePointer(new Point(100 + frame * 10, 200), tick);
                    scene.Update(MotionFrame(tick), tick, 1.0 / 120, null);
                    layer.Update(scene, false, width, height);
                    Render(scene, layer, width, height);
                }
                Check(layer.RenderCount == 1, $"{driver} cursor frames reuse music effects at {scale * 100}% DPI");
                scene.ApplySettings(Quiet with { CursorTrailEnabled = true });
                scene.Update(PartyTime.Audio.AudioFrame.Silent, now + Stopwatch.Frequency / 10, 0.1, null);
                layer.Update(scene, false, width, height);
                Check(layer.Visible, $"{driver} retains the previous music frame until its refresh is due");
                layer.Update(scene, true, width, height);
                Check(!layer.Visible && Render(scene, layer, width, height) > 0,
                    $"{driver} clearing music effects preserves the active cursor layer");
                scene.UpdatePointer(new Point(210, 200), now + Stopwatch.Frequency * 3);
                scene.Update(PartyTime.Audio.AudioFrame.Silent, now + Stopwatch.Frequency * 3, 0.2, null);
                Check(Render(scene, layer, width, height) == 0,
                    $"{driver} cached layers clear after the cursor expires");
            }
            context.SetDpi(96, 96);
        }
    }
}
