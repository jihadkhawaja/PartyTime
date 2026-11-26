using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using PartyTime.Settings;
using PartyTime.Visualization;

internal static partial class Program
{
    private static void CheckScreenPerformance()
    {
        long now = Stopwatch.GetTimestamp();
        foreach (var blend in Enum.GetValues<ScreenSampleBlend>().Where(blend => blend <= ScreenSampleBlend.Negative))
        {
            // Hold a patterned screenshot against a different desktop frame. A constant pixel
            // cannot detect blending after interpolation or fractional zoom on the renderer.
            using var source = new FakeCapture();
            using var sampler = new BeatScreenSampler(5, 1, source);
            var request = Request(blend, zoom: 12);
            byte[] held = [20, 200, 40, 180, 100];
            byte[] live = [250, 20, 195, 15, 175];
            source.Pattern = held;
            sampler.Update(request);
            sampler.CaptureFrame(now);
            source.Pattern = live;
            sampler.CaptureFrame(now);
            using var frame = sampler.AcquireFrame(blend)!;
            byte[] pixels = BitmapPixels.Read(frame.Bitmap);
            int[] crop = [0, 1, 2, 2, 3];
            for (int x = 0; x < held.Length; x++)
            {
                byte expected = BeatScreenSampler.BlendChannel(live[x], held[crop[x]], blend);
                Check(pixels[x * 4] == expected && pixels[x * 4 + 3] == 255,
                    $"{blend} blends opaque capture pixels after the WPF zoom crop at column {x}");
            }

            using var layer = new ScreenSampleLayer();
            layer.ShowFrame(sampler.AcquireFrame(blend), 0.5);
            using var output = Render(layer, 9, 9);
            pixels = BitmapPixels.Read(output);
            int center = (4 * 9 + 4) * 4;
            int faded = (int)Math.Round(BeatScreenSampler.BlendChannel(live[2], held[crop[2]], blend) * 0.5);
            Check(Math.Abs(pixels[center] - faded) <= 1 && Math.Abs(pixels[center + 3] - 128) <= 1,
                $"{blend} applies fade to the finished frame after blending");
            if (blend is ScreenSampleBlend.Additive or ScreenSampleBlend.Difference)
            {
                // At x=1, the 5 px source is sampled between columns 0 and 1.
                double fraction = 1.5 * 5 / 9 - 0.5;
                byte first = BeatScreenSampler.BlendChannel(live[0], held[crop[0]], blend);
                byte second = BeatScreenSampler.BlendChannel(live[1], held[crop[1]], blend);
                double scaled = (first + (second - first) * fraction) * 0.5;
                Check(Math.Abs(pixels[(4 * 9 + 1) * 4] - scaled) <= 2, $"{blend} interpolates the finished blend");
            }
            layer.ShowFrame(null, 0);
            using var cleared = Render(layer, 9, 9);
            Check(BitmapPixels.Read(cleared).All(b => b == 0), $"{blend} screen layer clears completely after silence");
        }
        ScreenSampleRequest Request(ScreenSampleBlend blend, long generation = 1, int zoom = 0) => new(new VisualizerSettings
            { ScreenSamplesEnabled = true, SampleBlend = blend, SampleOpacity = 100, SampleZoom = zoom }, now, generation);
        using (var source = new FakeCapture())
        using (var sampler = new BeatScreenSampler(8, 4, source))
        {
            sampler.Update(Request(ScreenSampleBlend.Normal)); sampler.CaptureFrame(now);
            for (int i = 0; i < 8; i++) sampler.CaptureFrame(now);
            Check(source.Reads == 1, "Normal screenshot animation captures only once per held generation");
            using var retained = sampler.AcquireFrame(ScreenSampleBlend.Normal)!;
            sampler.Update(Request(ScreenSampleBlend.Additive)); sampler.CaptureFrame(now);
            using var live = sampler.AcquireFrame(ScreenSampleBlend.Additive)!;
            Check(BitmapPixels.Read(live.Bitmap)[0] == 30 && source.Reads == 2, "Changing blend preserves the held sample and starts live capture");
            source.Frozen = true;
            sampler.CaptureFrame(now);
            using var unchanged = sampler.AcquireFrame(ScreenSampleBlend.Additive)!;
            Check(ReferenceEquals(live.Bitmap, unchanged.Bitmap), "Unchanged desktop frames reuse the same uploaded bitmap");
            sampler.Update(Request(ScreenSampleBlend.Negative)); sampler.CaptureFrame(now);
            using var negative = sampler.AcquireFrame(ScreenSampleBlend.Negative)!;
            Check(BitmapPixels.Read(negative.Bitmap)[0] == 245, "Negative mode inverts the held pixels once without recapturing");
            int reads = source.Reads;
            sampler.Update(Request(ScreenSampleBlend.Normal, 2)); sampler.CaptureFrame(now);
            Check(source.Reads == reads + 1 && BitmapPixels.Read(retained.Bitmap)[0] == 10,
                "New beats capture immediately while retained older frames remain valid");
            sampler.Dispose();
            Check(sampler.AcquireFrame(ScreenSampleBlend.Normal) == null && BitmapPixels.Read(retained.Bitmap)[0] == 10,
                "Stopping releases worker ownership without invalidating retained render frames");
        }

        using (var source = new FakeCapture())
        using (var sampler = new BeatScreenSampler(8, 4, source, sampleColors: true))
        {
            long step = Stopwatch.Frequency / 2;
            sampler.CaptureFrame(now);
            var colors = sampler.Colors;
            source.Frozen = true;
            sampler.CaptureFrame(now + step);
            Check(colors != null && ReferenceEquals(sampler.Colors, colors), "Unchanged desktop frames reuse the extracted color palette");
            source.Available = false;
            sampler.CaptureFrame(now + step * 2);
            source.Available = true;
            Check(sampler.Colors == null && sampler.CaptureFrame(now + step * 3) && sampler.Colors != null,
                "Palette sampling recovers after an unavailable frame with the same capture version");
        }
    }

    private sealed class FakeCapture : IScreenCaptureSource
    {
        public int Reads;
        public bool Frozen;
        public bool Available = true;
        public byte[]? Pattern;
        public string? Error => null;
        public long Version { get; private set; }
        public int MaximumFrameRate => 60;
        public bool ReadPixels(byte[] destination, bool retainPreviousPixels = false)
        {
            Reads++;
            if (!Available) return false;
            if (!Frozen) Version++;
            for (int i = 0; i < destination.Length; i += 4)
            {
                byte blue = Pattern is { Length: > 0 } pattern ? pattern[i / 4 % pattern.Length] : (byte)(Version * 10);
                destination[i] = blue;
                destination[i + 1] = 50;
                destination[i + 2] = 100;
                destination[i + 3] = 0;
            }
            return true;
        }
        public void Dispose() { }
    }

    private static void CheckNewScreenEffects()
    {
        long now = Stopwatch.GetTimestamp();
        var settings = new VisualizerSettings { ScreenSamplesEnabled = true, SampleOpacity = 100, SampleZoom = 0 };
        var blur = new ScreenSampleRequest(settings with { SampleBlend = ScreenSampleBlend.Blur, SampleEffectStrength = 1 }, now, 1);
        using var source = new FakeCapture { Pattern = Enumerable.Repeat((byte)0, 60).Concat(Enumerable.Repeat((byte)200, 60)).ToArray() };
        using var sampler = new BeatScreenSampler(120, 120, source);
        sampler.Update(blur);
        sampler.CaptureFrame(now);
        using var soft = sampler.AcquireFrame(ScreenSampleBlend.Blur)!;
        byte[] softPixels = BitmapPixels.Read(soft.Bitmap);
        Check(softPixels[59 * 4] is > 0 and < 200 && softPixels[10 * 4] == 0 && softPixels[100 * 4] == 200,
            "Blur softens edges and preserves flat colors");
        sampler.Update(blur with { Settings = blur.Settings with { SampleEffectStrength = 100 } });
        sampler.CaptureFrame(now);
        using var stronger = sampler.AcquireFrame(ScreenSampleBlend.Blur)!;
        Check(BitmapPixels.Read(stronger.Bitmap)[57 * 4] > softPixels[57 * 4] && source.Reads == 1,
            "Changing blur strength softens more pixels without capturing a new screenshot");
        sampler.CaptureFrame(now + Stopwatch.Frequency / 20);
        using var reused = sampler.AcquireFrame(ScreenSampleBlend.Blur)!;
        Check(ReferenceEquals(stronger.Bitmap, reused.Bitmap), "A held blur frame is reused during the fade");

        using var blocks = new BeatScreenSampler(5, 3, new FakeCapture { Pattern = [0, 200, 70, 90, 255] });
        blocks.Update(new ScreenSampleRequest(settings with { SampleBlend = ScreenSampleBlend.Pixelate }, now, 1));
        blocks.CaptureFrame(now);
        using var mosaic = blocks.AcquireFrame(ScreenSampleBlend.Pixelate)!;
        byte[] mosaicPixels = BitmapPixels.Read(mosaic.Bitmap);
        byte[] expectedBlocks = [100, 100, 80, 80, 255];
        Check(Enumerable.Range(0, 3).All(y => Enumerable.Range(0, 5)
            .All(x => mosaicPixels[(y * 5 + x) * 4] == expectedBlocks[x])),
            "Pixelate averages blocks and preserves the partial blocks at the edges");

        using var live = new FakeCapture { Pattern = [0, 10, 20, 30, 40, 50, 60, 70] };
        using var liveSampler = new BeatScreenSampler(8, 1, live);
        var zoom = new ScreenSampleRequest(settings with { SampleBlend = ScreenSampleBlend.ZoomOnly, SampleZoom = 12 }, now, 1);
        liveSampler.Update(zoom);
        liveSampler.CaptureFrame(now);
        live.Pattern = [0, 20, 40, 60, 80, 100, 120, 140];
        liveSampler.CaptureFrame(now + Stopwatch.Frequency / 30);
        using var current = liveSampler.AcquireFrame(ScreenSampleBlend.ZoomOnly)!;
        Check(live.Reads == 2 && BitmapPixels.Read(current.Bitmap)[7 * 4] == 140,
            "Zoom Only keeps using live pixels between capture beats");
        Check(Math.Abs(zoom.LiveZoom(now) - 1.12) < 0.0001 && zoom.LiveZoom(now + Stopwatch.Frequency / 2) < 1.02,
            "Live zoom decays from the beat peak toward the original scale");
        using var layer = new ScreenSampleLayer();
        layer.ShowFrame(liveSampler.AcquireFrame(ScreenSampleBlend.ZoomOnly), 1);
        using var actualSize = Render(layer, 80, 8);
        layer.ShowFrame(liveSampler.AcquireFrame(ScreenSampleBlend.ZoomOnly), 1, 2);
        using var cropped = Render(layer, 80, 8);
        Check(BitmapPixels.Read(cropped)[(4 * 80 + 5) * 4] > BitmapPixels.Read(actualSize)[(4 * 80 + 5) * 4] + 20,
            "The live preview can change the zoom crop without uploading a different frame");
        var zeroZoom = zoom with { Settings = zoom.Settings with { SampleZoom = 0 } };
        liveSampler.Update(zeroZoom);
        liveSampler.CaptureFrame(now);
        Check(zeroZoom.Opacity(now) == 0 && live.Reads == 2, "Zero zoom suppresses the effect and stops live capture");
    }
}
