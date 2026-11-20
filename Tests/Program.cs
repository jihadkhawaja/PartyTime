using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PartyTime.Audio;
using PartyTime.Settings;
using PartyTime.Views;
using PartyTime.Visualization;
using BitmapSource = Avalonia.Media.Imaging.Bitmap;

internal static partial class Program
{
    private static int _checks;
    private static readonly string Output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/feature-checks"));
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--benchmark-capture")) return RunNativePerformanceBenchmark();
            if (args.Contains("--benchmark-performance")) return RunPerformanceBenchmark(args);
            if (args.Contains("--native-smoke")) return RunNativeSmoke();
            AppBuilder.Configure<PartyTime.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia().SetupWithoutStarting();
            Directory.CreateDirectory(Output);
            CheckAudio(); CheckSettings();
            string image = CreateImage();
            CheckScene(image); CheckImagePlacement(image); CheckChromaKey();
            CheckSkiaRendering(image);
            CheckMotion(); CheckScreenSamples(); CheckScreenPerformance(); CheckNewScreenEffects(); CheckPresets(); CheckTransparency(image); CheckControls(image); CheckBulkControls(image);
            Console.WriteLine($"Passed {_checks} checks. Rendered checks: {Output}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new Exception(description);
        _checks++;
        Console.WriteLine("PASS " + description);
    }


    private static AudioFrame Tone(double frequency, FrequencyRange? focus = null)
    {
        var analyzer = new AudioAnalyzer();
        var samples = new float[AudioAnalyzer.SampleRate / 2];
        for (int i = 0; i < samples.Length / 2; i++)
            samples[i * 2] = samples[i * 2 + 1] = (float)(0.25 * Math.Sin(Math.Tau * frequency * i / AudioAnalyzer.SampleRate));
        return analyzer.Push(MemoryMarshal.AsBytes(samples.AsSpan()), Stopwatch.GetTimestamp(), focus ?? FrequencyRange.Full)!;
    }


    private static void CheckAudio()
    {
        foreach (var (hz, activeIndex) in new[] { (100, 0), (1000, 1), (6000, 2) })
        {
            var frame = Tone(hz);
            AudioSignal[] signals = [frame.Low, frame.Mid, frame.High];
            float active = signals[activeIndex].Level;
            Check(active > 0.2, $"{hz} Hz activates its assigned range");
            Check(signals.Where((_, index) => index != activeIndex).All(signal => signal.Level < active * 0.08),
                $"{hz} Hz stays isolated from other ranges");
            Check(signals[activeIndex].BeatTimestamp != 0 && signals.Where((_, index) => index != activeIndex).All(signal => signal.BeatTimestamp == 0),
                $"{hz} Hz onsets stay isolated from other ranges");
        }
        var focused = Tone(100, FrequencyRange.Treble);
        Check(focused.Focused.Level < 0.01 && focused.Low.Level > 0.2 && focused.Full.Level > 0.2,
            "Low and full responses remain available with a treble focus");
        var silent = new AudioAnalyzer().Push(new byte[4096 * 8], Stopwatch.GetTimestamp(), FrequencyRange.Full)!;
        Check(new[] { silent.Focused, silent.Full, silent.Low, silent.Mid, silent.High }.All(signal => signal.Level == 0 && signal.Bands.All(band => band == 0)),
            "Silence clears every frequency response");
    }


    private static void CheckSettings()
    {
        var legacy = VisualizerSettings.ReadSettings("{\"Style\":1,\"EdgeGlow\":false}");
        Check(legacy.Circle == CircleStyle.Orbit && legacy.Style == VisualStyle.None && legacy.Border == BorderStyle.None,
            "Legacy settings migrate without losing their visuals");
        Check(legacy.BarsReaction == AudioReaction.Focus && legacy.Images.Length == 0,
            "Existing settings keep their audio routing");
        var settings = new VisualizerSettings
        {
            CircleReaction = AudioReaction.Low, BorderReaction = AudioReaction.Mid, EffectsReaction = AudioReaction.High,
            Images = [new() { Name = "Transparent star", Path = "star.png", Layout = ImageLayout.ReplaceDots, Reaction = AudioReaction.High, X = 20, Count = 24,
                ChromaKeyEnabled = true, ChromaKeyColor = "#22AA44", ChromaKeyTolerance = 33 }]
        };
        var restored = VisualizerSettings.ReadSettings(JsonSerializer.Serialize(settings));
        Check(restored.CircleReaction == AudioReaction.Low && restored.BorderReaction == AudioReaction.Mid && restored.EffectsReaction == AudioReaction.High
            && restored.Images.SequenceEqual(settings.Images), "Image customization and per-layer ranges survive a settings round trip");
        var invalid = VisualizerSettings.ReadSettings("{\"BarsReaction\":99,\"Images\":[null,{\"Size\":999,\"Count\":-2,\"Opacity\":-9,\"Reaction\":99,\"Layout\":99},{}]}");
        Check(invalid.BarsReaction == AudioReaction.Focus && invalid.Images.Length == 2 && invalid.Images[0].Size == 50
            && invalid.Images[0].Count == 1 && invalid.Images[0].Opacity == 0 && invalid.Images[0].Reaction == AudioReaction.Focus,
            "Malformed image values and routing safely return to supported limits");
        var invalidKey = VisualizerSettings.ReadSettings("{\"Images\":[{\"ChromaKeyColor\":\"invalid\",\"ChromaKeyTolerance\":999}]}");
        Check(invalidKey.Images[0].ChromaKeyColor == "#00FF00" && invalidKey.Images[0].ChromaKeyTolerance == 100,
            "Invalid chroma key settings use a valid color and tolerance");
        var motion = new VisualizerSettings { ShapesEnabled = true, Shape = ShapeKind.Heart, ShapeMotion = ShapeMotion.Spiral,
            CursorTrailEnabled = true, CursorTrail = CursorTrailStyle.Shapes, CursorShape = ShapeKind.Hexagon, CursorTrailLength = 850 };
        Check(VisualizerSettings.ReadSettings(JsonSerializer.Serialize(motion)) == motion,
            "Shape and cursor preferences survive a settings round trip");
        var invalidMotion = VisualizerSettings.ReadSettings("{\"Shape\":99,\"ShapeMotion\":99,\"ShapeCount\":999,\"ShapeSize\":0,\"CursorTrail\":99,\"CursorTrailLength\":-9,\"CursorTrailSize\":999}");
        Check(invalidMotion.Shape == ShapeKind.Mixed && invalidMotion.ShapeMotion == ShapeMotion.Float && invalidMotion.ShapeCount == 96
            && invalidMotion.ShapeSize == 1 && invalidMotion.CursorTrail == CursorTrailStyle.Glow && invalidMotion.CursorTrailLength == 150
            && invalidMotion.CursorTrailSize == 40 && !legacy.ShapesEnabled && !legacy.CursorTrailEnabled,
            "Motion settings are bounded and existing settings keep new effects off");
    }


    private static string CreateImage()
    {
        // Eight by four image with transparent sides and a half-transparent red center.
        var pixels = new byte[8 * 4 * 4];
        for (int y = 0; y < 4; y++)
            for (int x = 2; x < 6; x++)
            {
                pixels[(y * 8 + x) * 4 + 2] = 255;
                pixels[(y * 8 + x) * 4 + 3] = 128;
            }
        var bitmap = FromStraightPixels(pixels, 8, 4);
        string path = Path.Combine(Output, "transparent-test.png");
        SavePng(bitmap, path);
        return path;
    }


    private static VisualizerSettings ImageSettings(string path) => new()
    {
        Style = VisualStyle.None, Circle = CircleStyle.None, Border = BorderStyle.None, Overlay = OverlayStyle.None,
        Intensity = 100,
        Images = [new() { Path = path, Size = 50, Opacity = 100, Reaction = AudioReaction.High }]
    };


    private static void CheckScene(string imagePath)
    {
        var scene = new Scene();
        var settings = ImageSettings(imagePath);
        var audio = Tone(6000);
        scene.ApplySettings(settings);
        Check(scene.Update(audio, audio.Timestamp, 0.2, null), "An image-only overlay stays active");
        var drawing = new RecordingDrawing();
        scene.Draw(drawing, 400, 300);
        Check(drawing.Images == 1 && drawing.LastBounds.Width == drawing.LastBounds.Height * 2,
            "Sticker renders once with its original aspect ratio");
        foreach (var layout in new[] { ImageLayout.Halo, ImageLayout.Scatter, ImageLayout.ReplaceDots })
        {
            scene = new Scene();
            scene.ApplySettings(settings with
            {
                Circle = layout == ImageLayout.ReplaceDots ? CircleStyle.DotHalo : CircleStyle.None,
                CircleReaction = AudioReaction.Low,
                Images = [settings.Images[0] with { Layout = layout, Count = 7 }]
            });
            scene.Update(audio, audio.Timestamp, 0.2, null);
            drawing = new RecordingDrawing();
            scene.Draw(drawing, 400, 300);
            Check(drawing.Images == 7, $"{layout} renders the selected number of copies using the image's range");
        }
        scene = new Scene();
        scene.ApplySettings(settings with { Images = [settings.Images[0] with { Reaction = AudioReaction.Low }] });
        Check(!scene.Update(audio, audio.Timestamp, 0.2, null), "An inactive assigned range lets the overlay idle");
        drawing = new RecordingDrawing();
        scene.Draw(drawing, 400, 300);
        Check(drawing.Images == 0, "Low-routed images ignore high frequency audio");
        scene = new Scene();
        scene.ApplySettings(settings with { Images = [], Circle = CircleStyle.PulseRings, CircleReaction = AudioReaction.Low });
        scene.Update(audio, audio.Timestamp, 0.2, null);
        drawing = new RecordingDrawing();
        scene.Draw(drawing, 400, 300);
        Check(drawing.Shapes == 0, "Low-routed circles ignore high frequency audio");
        scene.ApplySettings(settings with { Images = [], Circle = CircleStyle.PulseRings, CircleReaction = AudioReaction.High });
        scene.Update(audio, audio.Timestamp, 0.2, null);
        scene.Draw(drawing, 400, 300);
        Check(drawing.Shapes == 3, "High-routed circles respond to high frequency audio");
        scene = new Scene();
        scene.ApplySettings(settings with { Images = [settings.Images[0] with { Enabled = false }] });
        Check(!scene.Update(audio, audio.Timestamp, 0.2, null), "Disabling the only image hides the overlay");
        Check(OverlayImageStore.Load(Path.Combine(Output, "missing.png")) == null, "Missing images do not crash rendering");
        string invalid = Path.Combine(Output, "invalid.png");
        File.WriteAllText(invalid, "not an image");
        Check(OverlayImageStore.Load(invalid) == null, "Corrupt image files do not crash rendering");
    }


    private static void CheckImagePlacement(string path)
    {
        string otherPath = Path.Combine(Output, "transparent-other.png");
        File.Copy(path, otherPath, overwrite: true);
        var first = new OverlayImageSettings { Path = path, Count = 3, Size = 50, Reaction = AudioReaction.Full };
        var second = new OverlayImageSettings { Path = otherPath, Count = 2, Size = 50, Reaction = AudioReaction.Full };
        var audio = Tone(1000);
        foreach (var (layout, sparks) in new[] { (ImageLayout.Sticker, false), (ImageLayout.Halo, false),
            (ImageLayout.Scatter, false), (ImageLayout.ReplaceDots, false), (ImageLayout.ReplaceDots, true) })
        {
            foreach (var (width, height) in new[] { (1100, 500), (500, 1100) })
            {
                var scene = new Scene();
                scene.ApplySettings(ImageSettings(path) with
                {
                    Circle = layout == ImageLayout.ReplaceDots && !sparks ? CircleStyle.DotHalo : CircleStyle.None,
                    Overlay = sparks ? OverlayStyle.Sparks : OverlayStyle.None,
                    Images = [first with { Layout = layout }, second with { Layout = layout }]
                });
                scene.Update(audio, audio.Timestamp, 0.2, null);
                var drawing = new RecordingDrawing();
                scene.Draw(drawing, width, height);
                int[] expected = layout == ImageLayout.Sticker ? [0, 1] : [0, 1, 0, 1, 0];
                var bitmaps = new[] { OverlayImageStore.Load(path), OverlayImageStore.Load(otherPath) };
                Check(drawing.ImageDraws.Count == expected.Length && drawing.ImageDraws.Select((entry, index) =>
                    ReferenceEquals(entry.Bitmap, bitmaps[expected[index]])).All(value => value),
                    $"{layout}{(sparks ? " sparks" : "")} interleaves images in list order at {width}×{height}");
                Check(NoOverlaps(drawing.ImageDraws.Select(entry => entry.Bounds).ToArray()),
                    $"{layout}{(sparks ? " sparks" : "")} reserves separate image bounds at {width}×{height}");
            }
        }
        var custom = new Scene();
        custom.ApplySettings(ImageSettings(path) with { Images = [first with { X = 20, Y = 80 }, second with { X = 80, Y = 20 }] });
        custom.Update(audio, audio.Timestamp, 0.2, null);
        var positions = new RecordingDrawing();
        custom.Draw(positions, 1000, 1000);
        Check(positions.ImageDraws[0].Bounds.X + positions.ImageDraws[0].Bounds.Width / 2 == 200
            && positions.ImageDraws[1].Bounds.X + positions.ImageDraws[1].Bounds.Width / 2 == 800,
            "Separately positioned stickers retain their chosen anchors");
        var disabled = new Scene();
        disabled.ApplySettings(ImageSettings(path) with { Images = [first with { Enabled = false }, second] });
        disabled.Update(audio, audio.Timestamp, 0.2, null);
        positions = new RecordingDrawing();
        disabled.Draw(positions, 1000, 1000);
        Check(positions.Images == 1 && positions.LastBounds.X + positions.LastBounds.Width / 2 == 500,
            "Disabled images do not occupy layout slots");
    }


    private static bool NoOverlaps(Rect[] rectangles)
    {
        for (int i = 0; i < rectangles.Length; i++)
            for (int j = i + 1; j < rectangles.Length; j++)
                if (rectangles[i].Intersects(rectangles[j]))
                    return false;
        return true;
    }


    private static void CheckChromaKey()
    {
        byte[] pixels = [0, 255, 0, 255, 0, 0, 255, 255, 0, 255, 0, 128, 0, 0, 255, 128,
            0, 0, 0, 0, 0, 230, 0, 255, 0, 205, 0, 128];
        string path = Path.Combine(Output, "chroma-source.png");
        SavePng(FromStraightPixels(pixels, 7, 1), path);
        var settings = new OverlayImageSettings { Path = path, ChromaKeyEnabled = true, ChromaKeyColor = "#00FF00", ChromaKeyTolerance = 0 };
        var original = OverlayImageStore.Load(path)!;
        var keyed = OverlayImageStore.Load(settings)!;
        var result = new byte[28];
        result = BitmapPixels.Read(keyed);
        Check(result[3] == 0 && result[11] == 0 && result[19] == 0, "Chroma key removes matching opaque and semitransparent pixels");
        Check(result[7] == 255 && result[15] == 128 && result[23] == 255,
            "Chroma key preserves other colors and their existing alpha");
        Check(OverlayImageStore.ReadPixel(original, 0, 0).A == 255, "Chroma key leaves the original image intact");
        Check(ReferenceEquals(keyed, OverlayImageStore.Load(settings with { Size = 33 })), "Unchanged color settings reuse the processed bitmap");
        Check(ReferenceEquals(original, OverlayImageStore.Load(settings with { ChromaKeyEnabled = false })), "Disabling chroma key restores the original bitmap");
        var tolerant = OverlayImageStore.Load(settings with { ChromaKeyTolerance = 10 })!;
        result = BitmapPixels.Read(tolerant);
        Check(result[23] == 0 && result[27] > 0 && result[27] < 128 && result[25] <= result[27],
            "Tolerance removes nearby colors and feathers edges with valid premultiplied alpha");
        var red = OverlayImageStore.ReadPixel(original, 3, 0);
        Check(red.R == 255 && red.A == 128, "The eyedropper recovers RGB from semitransparent pixels");
        var picked = ControlWindow.PickPreviewColor(original, new Point(30, 100), 140, 200);
        Check(picked is { R: 255, G: 0 }, "The eyedropper maps a scaled preview click to its source pixel");
        Check(ControlWindow.PickPreviewColor(original, new Point(30, 10), 140, 200) == null
            && ControlWindow.PickPreviewColor(original, new Point(140, 100), 140, 200) == null,
            "The eyedropper ignores preview letterboxing and out-of-bounds clicks");
        var scene = new Scene();
        scene.ApplySettings(ImageSettings(path) with { Images = [settings with { Reaction = AudioReaction.Full }] });
        var audio = Tone(1000);
        scene.Update(audio, audio.Timestamp, 0.2, null);
        var drawing = new RecordingDrawing();
        scene.Draw(drawing, 400, 300);
        Check(drawing.Images == 1 && OverlayImageStore.ReadPixel(drawing.ImageDraws[0].Bitmap, 0, 0).A == 0,
            "The overlay draws the same color-removed bitmap as the preview");
        SavePng(tolerant, Path.Combine(Output, "chroma-result.png"));
    }


    private sealed class RecordingDrawing : ISceneDrawing
    {
        public int Images, Shapes;
        public Rect LastBounds;
        public readonly List<(BitmapSource Bitmap, Rect Bounds)> ImageDraws = [];
        public readonly List<Point> Centers = [];
        public readonly List<Point[]> PolygonVertices = [];
        public int FilledPolygons;
        public void DrawColoredLine(Point start, Point end, Color color, double opacity, double thickness) => Shapes++;
        public void DrawGlow(Point center, double radius, Color color, double opacity)
        {
            Shapes += 2;
            Centers.Add(center);
        }
        public void DrawSymbol(ShapeKind shape, Point center, double radius, double rotation, Color color, double opacity, bool filled)
        {
            if (shape == ShapeKind.Circle) { Shapes++; Centers.Add(center); return; }
            var source = SymbolGeometry.Points(shape);
            var points = new Point[source.Length];
            var (sin, cos) = Math.SinCos(rotation);
            for (int i = 0; i < source.Length; i++)
                points[i] = new(center.X + radius * (source[i].X * cos - source[i].Y * sin), center.Y + radius * (source[i].X * sin + source[i].Y * cos));
            DrawPolygon(points, filled ? new ImmutableSolidColorBrush(Colors.White) : null, null);
        }
        public void SetImages(ReadOnlySpan<BitmapSource> images) { }
        public void DrawImage(BitmapSource image, Rect bounds, double opacity) { Images++; LastBounds = bounds; ImageDraws.Add((image, bounds)); }
        public void DrawRectangle(IBrush brush, Rect bounds) => Shapes++;
        public void DrawRoundedRectangle(IBrush? brush, IPen? pen, Rect bounds, double radius) => Shapes++;
        public void DrawEllipse(IBrush? brush, IPen? pen, Point center, double radius) { Shapes++; Centers.Add(center); }
        public void DrawLine(IPen pen, Point start, Point end) => Shapes++;
        public void DrawPolygon(ReadOnlySpan<Point> points, IBrush? brush, IPen? pen)
        {
            Shapes++;
            PolygonVertices.Add(points.ToArray());
            if (brush != null) FilledPolygons++;
        }
        public void DrawCurve(Point start, ReadOnlySpan<(Point Control, Point End)> segments, IPen halo, IPen stroke) => Shapes++;
    }
    private static Bitmap FromStraightPixels(byte[] pixels, int width, int height)
    {
        var premultiplied = pixels.ToArray();
        for (int i = 0; i < pixels.Length; i += 4)
            for (int c = 0; c < 3; c++) premultiplied[i + c] = (byte)((pixels[i + c] * pixels[i + 3] + 127) / 255);
        return BitmapPixels.Create(premultiplied, width, height);
    }
    private static void SavePng(Bitmap bitmap, string path) => bitmap.Save(path, new PngBitmapEncoderOptions());
}
