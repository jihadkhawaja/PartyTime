using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using PartyTime.Settings;
using PartyTime.Visualization;

internal static partial class Program
{
    private static void CheckSkiaRendering(string imagePath)
    {
        foreach (var preset in VisualPresets.All)
        {
            var scene = new Scene(); scene.ApplySettings(preset.Apply(new()));
            long now = Stopwatch.GetTimestamp();
            for (int i = 0; i < 50; i++) scene.UpdatePointer(new Point(30 + i * 6, 170 + Math.Sin(i / 5.0) * 40), now - (49 - i) * Stopwatch.Frequency / 200);
            scene.Update(MotionFrame(now), now, 0.1, null);
            using var renderer = new SkiaSceneRenderer();
            var native = new DrawingControl(context => context.Custom(new ScenePictureOperation(new Rect(0, 0, 400, 300), renderer.Record(scene, 400, 300, includeCursor: true))));
            var reference = new DrawingControl(context => scene.Draw(context, 400, 300));
            using var actual = Render(native, 400, 300);
            using var expected = Render(reference, 400, 300);
            var a = BitmapPixels.Read(actual); var b = BitmapPixels.Read(expected);
            double error = 0;
            for (int i = 0; i < a.Length; i++) error += Math.Abs(a[i] - b[i]);
            error /= a.Length;
            Check(error < 3, $"{preset.Name} native picture matches Avalonia reference (mean channel error {error:F3}/255)");
            SavePng(actual, System.IO.Path.Combine(Output, "skia-" + preset.Name.Replace(' ', '-') + ".png"));
        }
        var imageScene = new Scene(); imageScene.ApplySettings(ImageSettings(imagePath));
        var audio = Tone(6000); imageScene.Update(audio, audio.Timestamp, 0.1, null);
        using var imageRenderer = new SkiaSceneRenderer();
        var imageControl = new DrawingControl(context => context.Custom(new ScenePictureOperation(new Rect(0, 0, 400, 300), imageRenderer.Record(imageScene, 400, 300))));
        using var imageOutput = Render(imageControl, 400, 300);
        Check(BitmapPixels.Read(imageOutput).Any(b => b != 0), "Native picture renderer preserves transparent imported artwork");

        var random = new Random(42);
        foreach (var blend in Enum.GetValues<ScreenSampleBlend>().Where(blend => blend <= ScreenSampleBlend.Negative))
            foreach (double zoom in new[] { 1.0, 1.04, 2.0 })
                foreach (int width in new[] { 1, 7, 33, 960 })
                {
                    const int height = 5;
                    var bg = new byte[width * height * 4]; var sample = new byte[bg.Length]; var output = new byte[bg.Length];
                    random.NextBytes(bg); random.NextBytes(sample);
                    BeatScreenSampler.BlendPixels(bg, sample, output, width, height, blend, zoom);
                    bool equal = true;
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                        {
                            int sx = Math.Clamp((int)((x - width / 2.0) / zoom + width / 2.0), 0, width - 1);
                            int sy = Math.Clamp((int)((y - height / 2.0) / zoom + height / 2.0), 0, height - 1);
                            int target = (y * width + x) * 4, source = (sy * width + sx) * 4;
                            for (int c = 0; c < 3; c++) equal &= output[target + c] == BeatScreenSampler.BlendChannel(bg[target + c], sample[source + c], blend);
                            equal &= output[target + 3] == 255;
                        }
                    Check(equal, $"{blend} optimized CPU kernel matches scalar pixels at width {width}, zoom {zoom}");
                }
    }
}
