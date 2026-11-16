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
        foreach (var blend in Enum.GetValues<ScreenSampleBlend>())
        {
            using var bg = new SharedScreenBitmap(BitmapPixels.Create([102, 102, 102, 255], 1, 1));
            byte value = blend == ScreenSampleBlend.Negative ? (byte)95 : (byte)160;
            using var held = new SharedScreenBitmap(BitmapPixels.Create([value, value, value, 255], 1, 1));
            using var layer = new ScreenSampleLayer();
            layer.ShowFrame(new ScreenSampleFrame(held, bg, blend == ScreenSampleBlend.Negative), blend, 1, 0.5);
            using var output = Render(layer, 32, 32);
            byte[] pixels = BitmapPixels.Read(output);
            int center = (16 * 32 + 16) * 4;
            int expected = (int)Math.Round(BeatScreenSampler.BlendChannel(102, 160, blend) * 0.5);
            Check(Math.Abs(pixels[center] - expected) <= 1 && Math.Abs(pixels[center + 3] - 128) <= 1,
                $"{blend} renderer matches CPU blend and applies opacity to the complete result");
            layer.ShowFrame(null, blend, 1, 0);
            using var cleared = Render(layer, 32, 32);
            Check(BitmapPixels.Read(cleared).All(b => b == 0), $"{blend} screen layer clears completely after silence");
        }
        long now = Stopwatch.GetTimestamp();
        ScreenSampleRequest Request(ScreenSampleBlend blend, long generation = 1) => new(new VisualizerSettings
            { ScreenSamplesEnabled = true, SampleBlend = blend, SampleOpacity = 100, SampleZoom = 4 }, now, generation);
        using (var source = new FakeCapture())
        using (var sampler = new BeatScreenSampler(8, 4, source))
        {
            sampler.Update(Request(ScreenSampleBlend.Normal)); sampler.CaptureFrame();
            for (int i = 0; i < 8; i++) sampler.CaptureFrame();
            Check(source.Reads == 1, "Normal screenshot animation captures only once per held generation");
            using var retained = sampler.AcquireFrame(ScreenSampleBlend.Normal)!;
            var held = retained.Held;
            sampler.Update(Request(ScreenSampleBlend.Additive)); sampler.CaptureFrame();
            using var live = sampler.AcquireFrame(ScreenSampleBlend.Additive)!;
            Check(ReferenceEquals(held, live.Held) && source.Reads == 2, "Changing blend preserves the held sample and starts live capture");
            source.Frozen = true;
            sampler.CaptureFrame();
            using var unchanged = sampler.AcquireFrame(ScreenSampleBlend.Additive)!;
            Check(ReferenceEquals(live.Current, unchanged.Current), "Unchanged desktop frames reuse the same uploaded bitmap");
            sampler.Update(Request(ScreenSampleBlend.Negative)); sampler.CaptureFrame();
            using var negative = sampler.AcquireFrame(ScreenSampleBlend.Negative)!;
            Check(BitmapPixels.Read(negative.Held)[0] == 245, "Negative mode inverts the held pixels once without recapturing");
            int reads = source.Reads;
            sampler.Update(Request(ScreenSampleBlend.Normal, 2)); sampler.CaptureFrame();
            Check(source.Reads == reads + 1 && BitmapPixels.Read(retained.Held)[0] == 10,
                "New beats capture immediately while retained older frames remain valid");
            sampler.Dispose();
            Check(sampler.AcquireFrame(ScreenSampleBlend.Normal) == null && BitmapPixels.Read(retained.Held)[0] == 10,
                "Stopping releases worker ownership without invalidating retained render frames");
        }
    }

    private sealed class FakeCapture : IScreenCaptureSource
    {
        public int Reads;
        public bool Frozen;
        public string? Error => null;
        public long Version { get; private set; }
        public int MaximumFrameRate => 60;
        public bool ReadPixels(byte[] destination)
        {
            Reads++;
            if (!Frozen) Version++;
            for (int i = 0; i < destination.Length; i += 4)
            {
                destination[i] = (byte)(Version * 10); destination[i + 1] = 50; destination[i + 2] = 100; destination[i + 3] = 255;
            }
            return true;
        }
        public void Dispose() { }
    }
}
