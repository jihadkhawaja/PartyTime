using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PartyTime.Audio;
using PartyTime.Settings;
using PartyTime.Views;
using PartyTime.Visualization;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Colors = System.Windows.Media.Colors;
using Color = System.Windows.Media.Color;

internal static partial class Program
{
    private static int _checks;
    private static readonly string Output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/feature-checks"));

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--benchmark-cursor"))
            {
                RunCursorBenchmark();
                return 0;
            }
            RunChecks();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void RunChecks()
    {
        Directory.CreateDirectory(Output);
        CheckAudio();
        CheckSettings();
        string imagePath = CreateImage();
        CheckScene(imagePath);
        CheckImagePlacement(imagePath);
        CheckChromaKey();
        CheckMotion();
        CheckPresets();
        CheckTransparency(imagePath);
        CheckGpu(imagePath);
        CheckGpuMotion();
        CheckControls(imagePath);
        Console.WriteLine($"Passed {_checks} checks. Visual checks: {Output}");
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
        var bitmap = BitmapSource.Create(8, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 32);
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
                if (rectangles[i].IntersectsWith(rectangles[j]))
                    return false;
        return true;
    }

    private static void CheckChromaKey()
    {
        byte[] pixels = [0, 255, 0, 255, 0, 0, 255, 255, 0, 255, 0, 128, 0, 0, 255, 128,
            0, 0, 0, 0, 0, 230, 0, 255, 0, 205, 0, 128];
        string path = Path.Combine(Output, "chroma-source.png");
        SavePng(BitmapSource.Create(7, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 28), path);
        var settings = new OverlayImageSettings { Path = path, ChromaKeyEnabled = true, ChromaKeyColor = "#00FF00", ChromaKeyTolerance = 0 };
        var original = OverlayImageStore.Load(path)!;
        var keyed = OverlayImageStore.Load(settings)!;
        var result = new byte[28];
        keyed.CopyPixels(result, 28, 0);
        Check(result[3] == 0 && result[11] == 0 && result[19] == 0, "Chroma key removes matching opaque and semitransparent pixels");
        Check(result[7] == 255 && result[15] == 128 && result[23] == 255,
            "Chroma key preserves other colors and their existing alpha");
        Check(OverlayImageStore.ReadPixel(original, 0, 0).A == 255, "Chroma key leaves the original image intact");
        Check(ReferenceEquals(keyed, OverlayImageStore.Load(settings with { Size = 33 })), "Unchanged color settings reuse the processed bitmap");
        Check(ReferenceEquals(original, OverlayImageStore.Load(settings with { ChromaKeyEnabled = false })), "Disabling chroma key restores the original bitmap");
        var tolerant = OverlayImageStore.Load(settings with { ChromaKeyTolerance = 10 })!;
        tolerant.CopyPixels(result, 28, 0);
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

    private static void CheckTransparency(string path)
    {
        var bitmap = OverlayImageStore.Load(path)!;
        Check(bitmap.PixelWidth == 8 && bitmap.PixelHeight == 4 && bitmap.Format == PixelFormats.Pbgra32,
            "Import preserves small image dimensions and converts to premultiplied alpha");
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            new WpfSceneDrawing(context).DrawImage(bitmap, new Rect(0, 0, 80, 40), 0.5);
        var target = new RenderTargetBitmap(80, 40, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var pixels = new byte[80 * 40 * 4];
        target.CopyPixels(pixels, 80 * 4, 0);
        Check(pixels[3] == 0 && Math.Abs(pixels[(20 * 80 + 40) * 4 + 3] - 64) <= 1,
            "WPF preserves transparent pixels and combines image alpha with opacity");
        SavePng(target, Path.Combine(Output, "wpf-alpha.png"));
    }

    private static unsafe void CheckGpu(string path)
    {
        D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, null,
            out ID3D11Device? device, out Vortice.Direct3D.FeatureLevel _).CheckError();
        using var gpu = device!;
        using var dxgi = gpu.QueryInterface<IDXGIDevice>();
        using var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        using var d2dDevice = factory.CreateDevice(dxgi);
        using var context = d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        using var drawing = new GpuSceneDrawing(factory, context);
        var format = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        using var target = context.CreateBitmap(new SizeI(80, 40), IntPtr.Zero, 0, new BitmapProperties1(format, 96, 96, BitmapOptions.Target));
        context.Target = target;
        context.BeginDraw();
        context.Clear(new Color4(0, 0, 0, 0));
        var bitmap = OverlayImageStore.Load(path)!;
        drawing.SetImages([bitmap]);
        drawing.DrawImage(bitmap, new Rect(0, 0, 80, 40), 0.5);
        drawing.DrawPolygon([new Point(5, 5), new Point(15, 5), new Point(10, 15)],
            new SolidColorBrush(Colors.Blue), new Pen(new SolidColorBrush(Colors.Orange), 1));
        context.EndDraw().CheckError();
        context.Target = null;
        using var readback = context.CreateBitmap(new SizeI(80, 40), IntPtr.Zero, 0,
            new BitmapProperties1(format, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        readback.CopyFromBitmap(target);
        var mapped = readback.Map(MapOptions.Read);
        try
        {
            var data = (byte*)mapped.Bits;
            Check(data[3] == 0 && Math.Abs(data[20 * mapped.Pitch + 40 * 4 + 3] - 64) <= 1,
                "Direct2D preserves transparent pixels and combines image alpha with opacity");
        }
        finally { readback.Unmap(); }
        drawing.SetImages([]);
        Check(true, "Direct2D releases removed image textures");
    }

    private static void CheckControls(string imagePath)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new ControlWindow(ImageSettings(imagePath), persistSettings: false)
        {
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30_000, Top = -30_000
        };
        window.Show();
        Check(window.FindName("ImageList") is ListBox && window.FindName("BarsReaction") is ComboBox { Items.Count: 5 },
            "Images and frequency routing controls initialize");
        window.Measure(new System.Windows.Size(584, 744));
        window.Arrange(new Rect(0, 0, 584, 744));
        var tabs = FindTabs((DependencyObject)window.Content);
        foreach (var tab in tabs.Items.Cast<TabItem>().Where(tab => tab.Header is "Images" or "Audio"))
        {
            tabs.SelectedItem = tab;
            window.Measure(new System.Windows.Size(584, 744));
            window.Arrange(new Rect(0, 0, 584, 744));
            window.UpdateLayout();
            var target = new RenderTargetBitmap(584, 744, 96, 96, PixelFormats.Pbgra32);
            target.Render((Visual)window.Content);
            SavePng(target, Path.Combine(Output, $"controls-{tab.Header}.png"));
            ((ScrollViewer)tab.Content).ScrollToEnd();
            window.UpdateLayout();
            target = new RenderTargetBitmap(584, 744, 96, 96, PixelFormats.Pbgra32);
            target.Render((Visual)window.Content);
            SavePng(target, Path.Combine(Output, $"controls-{tab.Header}-bottom.png"));
        }
        try
        {
            ((CheckBox)window.FindName("ImageChromaKey")).IsChecked = true;
            ((TextBox)window.FindName("ImageKeyColor")).Text = "#FF0000";
            ((Slider)window.FindName("ImageKeyTolerance")).Value = 0;
            var preview = (System.Windows.Controls.Image)window.FindName("ImagePreview");
            Check(OverlayImageStore.ReadPixel((BitmapSource)preview.Source, 3, 0).A == 0,
                "The image preview updates when color removal is enabled");
            ((Button)window.FindName("PickImageColorButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(OverlayImageStore.ReadPixel((BitmapSource)preview.Source, 3, 0).A == 128
                && preview.Cursor == System.Windows.Input.Cursors.Cross,
                "The eyedropper shows the original image with a crosshair");
            ((Button)window.FindName("PickImageColorButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(OverlayImageStore.ReadPixel((BitmapSource)preview.Source, 3, 0).A == 0,
                "Canceling the eyedropper restores the processed preview");
            tabs.SelectedItem = tabs.Items.Cast<TabItem>().First(tab => tab.Header is "Images");
            window.UpdateLayout();
            var imageTab = (TabItem)tabs.SelectedItem;
            var scroll = (ScrollViewer)imageTab.Content;
            scroll.ScrollToVerticalOffset(260);
            window.UpdateLayout();
            var chromaPreview = new RenderTargetBitmap(584, 744, 96, 96, PixelFormats.Pbgra32);
            chromaPreview.Render((Visual)window.Content);
            SavePng(chromaPreview, Path.Combine(Output, "controls-Images-chroma.png"));
            ((TextBox)window.FindName("ImageKeyColor")).Text = "invalid";
            Check(((FrameworkElement)window.FindName("ImageKeyColorError")).Visibility == Visibility.Visible,
                "Invalid color input shows an inline error without changing the applied key");
            ((TextBox)window.FindName("ImageKeyColor")).Text = "#FF0000";
            ((ComboBox)window.FindName("CircleReaction")).SelectedIndex = (int)AudioReaction.Low;
            ((ComboBox)window.FindName("ImageReaction")).SelectedIndex = (int)AudioReaction.Mid;
            ((Slider)window.FindName("ImageSize")).Value = 23;
            ((TextBox)window.FindName("ImageName")).Text = "Renamed image";
            var current = (VisualizerSettings)typeof(ControlWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Check(current.CircleReaction == AudioReaction.Low && current.Images[0].Reaction == AudioReaction.Mid
                && current.Images[0].Size == 23 && current.Images[0].Name == "Renamed image"
                && current.Images[0].ChromaKeyEnabled && current.Images[0].ChromaKeyColor == "#FF0000",
                "Editing controls updates image customization and layer routing together");
            ((Button)window.FindName("RemoveImageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((ListBox)window.FindName("ImageList")).Items.Count == 0
                && ((FrameworkElement)window.FindName("ImageEditor")).Visibility == Visibility.Collapsed,
                "Removing an image clears its selection and editor");
        }
        finally
        {
            // These UI checks use injected settings and must never save over the user's preferences.
            ((System.Windows.Threading.DispatcherTimer)typeof(ControlWindow).GetField("_saveTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!).Stop();
        }
        CheckBulkControls(imagePath);
        CheckOverlayComposition();
        CheckMotionControls(imagePath);
        app.Shutdown();
    }

    private static void CheckBulkControls(string imagePath)
    {
        OverlayImageSettings[] images =
        [
            new() { Name = "First", Path = imagePath, Size = 10, Opacity = 30, Reaction = AudioReaction.Low, X = 15,
                ChromaKeyEnabled = true, ChromaKeyColor = "#FF0000", Count = 3 },
            new() { Name = "Second", Path = imagePath, Size = 25, Opacity = 70, Reaction = AudioReaction.High, X = 65,
                Enabled = false, Layout = ImageLayout.Halo, ChromaKeyColor = "#0000FF", Count = 5 },
            new() { Name = "Third", Path = imagePath, Size = 40, Opacity = 90, Reaction = AudioReaction.Mid, X = 85,
                Layout = ImageLayout.Scatter, Count = 7 }
        ];
        var window = new ControlWindow(ImageSettings(imagePath) with { Images = images }, persistSettings: false)
        {
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30_000, Top = -30_000
        };
        var list = (ListBox)window.FindName("ImageList");
        T Control<T>(string name) => (T)window.FindName(name);
        VisualizerSettings Current() => (VisualizerSettings)typeof(ControlWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        try
        {
            list.SelectedItems.Add(list.Items[1]);
            Check(list.SelectionMode == SelectionMode.Extended && list.SelectedItems.Count == 2
                && !Control<TextBox>("ImageName").IsEnabled && Current().Images.SequenceEqual(images),
                "Multi-selection loads the editor without changing image settings or names");
            Check(Control<ComboBox>("ImageReaction").SelectedIndex == -1 && Control<ComboBox>("ImagePlacement").SelectedIndex == -1
                && Control<CheckBox>("ImageEnabled").IsChecked == null && Control<CheckBox>("ImageChromaKey").IsChecked == null
                && Control<TextBlock>("ImageSizeLabel").Text.Contains("Mixed"), "Mixed settings are identified across all selected images");
            Control<Slider>("ImageSize").Value = 32;
            Check(Current().Images[0] == images[0] with { Size = 32 } && Current().Images[1] == images[1] with { Size = 32 }
                && Current().Images[2] == images[2] && list.SelectedItems.Count == 2,
                "Bulk size changes preserve every other setting and unselected images");
            Check(Control<TextBlock>("ImageKeyColorError").Visibility == Visibility.Collapsed,
                "Editing another field does not treat a mixed key color as invalid");
            Control<ComboBox>("ImageReaction").SelectedIndex = (int)AudioReaction.Full;
            Check(Current().Images.Take(2).All(image => image.Reaction == AudioReaction.Full)
                && Current().Images[2].Reaction == AudioReaction.Mid, "Frequency assignment applies only to selected images");
            Control<TextBox>("ImageKeyColor").Text = "#AA4411";
            Check(Current().Images.Take(2).All(image => image.ChromaKeyColor == "#AA4411")
                && Current().Images[0].ChromaKeyEnabled && !Current().Images[1].ChromaKeyEnabled
                && Current().Images[2] == images[2], "Bulk key-color changes preserve each image's enable state");
            Control<CheckBox>("ImageChromaKey").IsChecked = true;
            Check(Current().Images.Take(2).All(image => image.ChromaKeyEnabled) && !Current().Images[2].ChromaKeyEnabled,
                "Chroma key can be enabled for a selected subset");
            var opacity = Control<Slider>("ImageOpacity");
            opacity.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            Check(Current().Images[0].Opacity == 30 && Current().Images[1].Opacity == 30 && Current().Images[2].Opacity == 90,
                "Clicking a mixed slider applies its shown value even when the thumb does not move");
            Control<CheckBox>("ImageEnabled").IsChecked = true;
            Control<Slider>("ImageX").Value = 44;
            Control<ComboBox>("ImagePlacement").SelectedIndex = (int)ImageLayout.Scatter;
            Check(Current().Images.Take(2).All(image => image.Enabled && image.X == 44 && image.Layout == ImageLayout.Scatter)
                && Current().Images[2] == images[2], "Visibility, position, and placement can be edited for a selected subset");
            Control<Button>("SelectAllImagesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(list.SelectedItems.Count == 3 && Control<TextBlock>("ImageSelectionSummary").Text.StartsWith("3 images selected"),
                "Select all selects the whole image library");
            Control<Slider>("ImageCount").Value = 17;
            Check(Current().Images.All(image => image.Count == 17) && Current().Images.Select(image => image.Name).SequenceEqual(images.Select(image => image.Name)),
                "Editing all images preserves their individual names");
            window.Show();
            var tabs = FindTabs((DependencyObject)window.Content);
            tabs.SelectedItem = tabs.Items.Cast<TabItem>().First(tab => tab.Header is "Images");
            window.Measure(new System.Windows.Size(584, 744));
            window.Arrange(new Rect(0, 0, 584, 744));
            window.UpdateLayout();
            var target = new RenderTargetBitmap(584, 744, 96, 96, PixelFormats.Pbgra32);
            target.Render((Visual)window.Content);
            SavePng(target, Path.Combine(Output, "controls-Images-bulk.png"));
            list.UnselectAll();
            list.SelectedItems.Add(list.Items[0]);
            list.SelectedItems.Add(list.Items[1]);
            Control<Button>("RemoveImageButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Current().Images.Length == 1 && Current().Images[0].Id == images[2].Id && list.SelectedItems.Count == 1
                && Control<TextBox>("ImageName").IsEnabled, "Remove selected deletes the subset and selects a remaining image");
            list.UnselectAll();
            Check(Control<FrameworkElement>("ImageEditor").Visibility == Visibility.Collapsed && !Control<Button>("RemoveImageButton").IsEnabled,
                "Clearing the selection hides the editor and disables removal");
            Control<Button>("SelectAllImagesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Control<Button>("RemoveImageButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Current().Images.Length == 0 && !Control<Button>("SelectAllImagesButton").IsEnabled,
                "Select all and Remove selected can clear the library");
        }
        finally
        {
            ((System.Windows.Threading.DispatcherTimer)typeof(ControlWindow).GetField("_saveTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!).Stop();
        }
    }

    private static TabControl FindTabs(DependencyObject root)
    {
        if (root is TabControl tabs) return tabs;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            try { return FindTabs(VisualTreeHelper.GetChild(root, i)); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException();
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
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
            DrawPolygon(points, filled ? Brushes.White : null, null);
        }
        public void SetImages(ReadOnlySpan<BitmapSource> images) { }
        public void DrawImage(BitmapSource image, Rect bounds, double opacity) { Images++; LastBounds = bounds; ImageDraws.Add((image, bounds)); }
        public void DrawRectangle(Brush brush, Rect bounds) => Shapes++;
        public void DrawRoundedRectangle(Brush? brush, Pen? pen, Rect bounds, double radius) => Shapes++;
        public void DrawEllipse(Brush? brush, Pen? pen, Point center, double radius) { Shapes++; Centers.Add(center); }
        public void DrawLine(Pen pen, Point start, Point end) => Shapes++;
        public void DrawPolygon(ReadOnlySpan<Point> points, Brush? brush, Pen? pen)
        {
            Shapes++;
            PolygonVertices.Add(points.ToArray());
            if (brush != null) FilledPolygons++;
        }
        public void DrawCurve(Point start, ReadOnlySpan<(Point Control, Point End)> segments, Pen halo, Pen stroke) => Shapes++;
    }
}
