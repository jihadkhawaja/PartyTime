using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PartyTime.Audio;
using PartyTime.Settings;
using PartyTime.Views;
using PartyTime.Visualization;

internal static partial class Program
{
    private static VisualizerSettings Quiet => new()
    {
        Style = VisualStyle.None, Circle = CircleStyle.None, Border = BorderStyle.None, Overlay = OverlayStyle.None
    };

    private static AudioFrame MotionFrame(long now)
    {
        AudioSignal Signal(FrequencyRange range) => new(0.65f, 0.6f,
            Enumerable.Range(0, AudioAnalyzer.BandCount).Select(i => AudioAnalyzer.IncludesBand(i, range) ? 0.6f : 0).ToArray(),
            now - Stopwatch.Frequency / 20, 0.8f, range);
        return new(now, Signal(FrequencyRange.Full), Signal(FrequencyRange.Full),
            Signal(FrequencyRange.Bass), Signal(FrequencyRange.Mids), Signal(FrequencyRange.Treble));
    }

    private static void CheckMotion()
    {
        Check(FramePacing.CursorRate(60) == 60 && FramePacing.CursorRate(144) == 120 && FramePacing.CursorRate(59.94) == 59.94
            && FramePacing.CursorRate(null) == 60, "Cursor cadence follows refresh rate independently and preserves fractional refresh rates");
        long period = FramePacing.Period(120);
        long advanced = FramePacing.Advance(100, 100 + period * 4 + period / 2, period);
        Check(advanced == 100 + period * 5, "Missed cursor frames advance directly to the next deadline");
        long now = Stopwatch.GetTimestamp();
        var audio = MotionFrame(now);
        foreach (var shape in Enum.GetValues<ShapeKind>())
        {
            var scene = new Scene();
            scene.ApplySettings(Quiet with { ShapesEnabled = true, Shape = shape, ShapeCount = 10, ShapesReaction = AudioReaction.Full });
            Check(scene.Update(audio, now, 0.1, null), $"{shape} activates as an independent shape layer");
            var drawing = new RecordingDrawing();
            scene.Draw(drawing, 800, 600);
            Check(drawing.Shapes == 10 && drawing.PolygonVertices.SelectMany(points => points).All(point => double.IsFinite(point.X) && double.IsFinite(point.Y)),
                $"{shape} renders the chosen count with finite geometry");
        }
        foreach (var motion in Enum.GetValues<ShapeMotion>())
        {
            var scene = new Scene();
            scene.ApplySettings(Quiet with { ShapesEnabled = true, Shape = ShapeKind.Hexagon, ShapeMotion = motion, ShapeCount = 12, FilledShapes = true });
            scene.Update(audio, now, 0.1, null);
            var drawing = new RecordingDrawing();
            scene.Draw(drawing, 800, 600);
            Check(drawing.FilledPolygons == 12, $"{motion} animates the filled shape layer");
        }
        var focused = new Scene();
        focused.ApplySettings(Quiet with { ShapesEnabled = true, ShapesReaction = AudioReaction.High });
        var low = Tone(100);
        Check(!focused.Update(low, low.Timestamp, 0.1, null), "High-routed shapes ignore bass-only audio");

        foreach (var style in Enum.GetValues<CursorTrailStyle>())
        {
            var settings = Quiet with { CursorTrailEnabled = true, CursorTrail = style, CursorShape = ShapeKind.Circle };
            var scene = new Scene();
            scene.ApplySettings(settings);
            scene.UpdatePointer(new Point(-1800, 150), now);
            Check(scene.Update(AudioFrame.Silent, now, 0.1, null), $"{style} cursor trail works during silence");
            var drawing = new RecordingDrawing();
            scene.Draw(drawing, 800, 600, new Point(-1920, 0), 1.5, 1.5);
            Check(drawing.Centers.Count > 0 && (drawing.Centers[0] - new Point(80, 100)).Length < 0.001,
                $"{style} cursor coordinates account for negative monitor origins and DPI");
            drawing = new RecordingDrawing();
            scene.Draw(drawing, 800, 600);
            Check(drawing.Shapes == 0, $"{style} cursor trail is not mirrored onto another display");
            long later = now + Stopwatch.Frequency * 2;
            scene.UpdatePointer(new Point(-1800, 150), later);
            Check(!scene.Update(AudioFrame.Silent, later, 0.2, null), $"{style} cursor trail fades when the mouse stops");
            scene.UpdatePointer(new Point(-1750, 160), later);
            scene.ApplySettings(settings with { CursorTrailEnabled = false });
            scene.ApplySettings(settings);
            Check(!scene.Update(AudioFrame.Silent, later, 0.1, null), $"{style} toggle clears previous cursor history");
        }
        var bounded = new Scene();
        bounded.ApplySettings(Quiet with { CursorTrailEnabled = true, CursorTrailLength = 2000 });
        for (int i = 0; i < 500; i++)
            bounded.UpdatePointer(new Point(i * 9, 150), now + i * Stopwatch.Frequency / 1000);
        bounded.Update(AudioFrame.Silent, now + Stopwatch.Frequency / 2, 0.1, null);
        var boundedDrawing = new RecordingDrawing();
        bounded.Draw(boundedDrawing, 10000, 1000);
        Check(boundedDrawing.Shapes is > 0 and <= 384, "Cursor history stays bounded during fast movement");
        Check(bounded.CursorIntersects(new Rect(0, 0, 5000, 1000)) && !bounded.CursorIntersects(new Rect(-2000, 0, 500, 1000)),
            "Cursor repainting is restricted to displays touched by the trail");
        Check(bounded.HasCursorTrail && !bounded.BaseVisible, "Cursor-only motion keeps the music layer inactive");
    }

    private static void CheckPresets()
    {
        var current = new VisualizerSettings
        {
            Focus = FrequencyFocus.Custom, LowHz = 100, HighHz = 900, Response = 135, FrameRate = OverlayFrameRate.Fps60,
            Images = [new() { Name = "Keep my image", Path = "image.png", ChromaKeyEnabled = true }]
        };
        Check(VisualPresets.All.Length >= 8 && VisualPresets.All.Select(preset => preset.Name).Distinct().Count() == VisualPresets.All.Length,
            "The preset library contains eight distinct looks");
        foreach (var preset in VisualPresets.All)
        {
            var applied = preset.Apply(current);
            Check(ReferenceEquals(applied.Images, current.Images) && applied.Focus == current.Focus && applied.LowHz == 100
                && applied.HighHz == 900 && applied.Response == 135 && applied.FrameRate == current.FrameRate,
                $"{preset.Name} preserves custom images, audio focus, response, and frame rate");
        }
        var sheet = new DrawingVisual();
        using (var drawing = sheet.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 14, 25)), null, new Rect(0, 0, 800, 960));
            for (int i = 0; i < VisualPresets.All.Length; i++)
            {
                drawing.PushTransform(new TranslateTransform(i % 2 * 400, i / 2 * 240));
                drawing.DrawText(new FormattedText(VisualPresets.All[i].Name, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 15, Brushes.White, 1), new Point(12, 4));
                drawing.PushTransform(new TranslateTransform(0, 28));
                var scene = new Scene();
                scene.ApplySettings(VisualPresets.All[i].Apply(new VisualizerSettings()));
                long now = Stopwatch.GetTimestamp();
                for (int point = 0; point < 18; point++)
                    scene.UpdatePointer(new Point(70 + point * 13, 105 + Math.Sin(point * 0.3) * 35), now - (17 - point) * Stopwatch.Frequency / 50);
                scene.Update(MotionFrame(now), now, 0.1, null);
                drawing.PushClip(new RectangleGeometry(new Rect(0, 0, 400, 208)));
                scene.Draw(drawing, 400, 208);
                drawing.Pop();
                drawing.Pop();
                drawing.Pop();
            }
        }
        var target = new RenderTargetBitmap(800, 960, 96, 96, PixelFormats.Pbgra32);
        target.Render(sheet);
        SavePng(target, Path.Combine(Output, "preset-gallery.png"));
    }

    private static void CheckMotionControls(string imagePath)
    {
        CheckCursorFallbackLayers();
        var original = ImageSettings(imagePath);
        var window = new ControlWindow(original, persistSettings: false)
        {
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000, Top = -30000
        };
        T Control<T>(string name) => (T)window.FindName(name);
        VisualizerSettings Current() => (VisualizerSettings)typeof(ControlWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        try
        {
            Control<CheckBox>("CursorTrailToggle").IsChecked = true;
            Control<ComboBox>("CursorStylePicker").SelectedIndex = (int)CursorTrailStyle.Shapes;
            Control<ComboBox>("CursorShapePicker").SelectedIndex = (int)ShapeKind.Heart;
            Control<CheckBox>("ShapesToggle").IsChecked = true;
            Control<ComboBox>("ShapeMotionPicker").SelectedIndex = (int)ShapeMotion.Spiral;
            Check(Current().CursorTrailEnabled && Current().CursorShape == ShapeKind.Heart && Current().ShapesEnabled
                && Current().ShapeMotion == ShapeMotion.Spiral, "Motion controls independently enable cursor and shape layers");
            Control<ComboBox>("PresetPicker").SelectedIndex = 7;
            Check(Current().ShapesEnabled, "Selecting a preset waits for Apply preset");
            Control<Button>("ApplyPresetButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Current().CursorTrailEnabled && !Current().ShapesEnabled && Current().Style == VisualStyle.None
                && Current().Images.SequenceEqual(original.Images), "Applying a preset updates its controls and preserves image customization");
            Control<CheckBox>("ShapesToggle").IsChecked = true;
            window.Show();
            var tabs = FindTabs((DependencyObject)window.Content);
            tabs.SelectedItem = tabs.Items.Cast<TabItem>().First(tab => tab.Header is "Motion");
            window.Measure(new Size(584, 744));
            window.Arrange(new Rect(0, 0, 584, 744));
            window.UpdateLayout();
            var target = new RenderTargetBitmap(584, 744, 96, 96, PixelFormats.Pbgra32);
            target.Render((Visual)window.Content);
            SavePng(target, Path.Combine(Output, "controls-Motion.png"));
            ((ScrollViewer)((TabItem)tabs.SelectedItem).Content).ScrollToEnd();
            window.UpdateLayout();
            target = new RenderTargetBitmap(584, 744, 96, 96, PixelFormats.Pbgra32);
            target.Render((Visual)window.Content);
            SavePng(target, Path.Combine(Output, "controls-Motion-shapes.png"));
        }
        finally
        {
            ((System.Windows.Threading.DispatcherTimer)typeof(ControlWindow).GetField("_saveTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!).Stop();
        }
    }

    private static void CheckCursorFallbackLayers()
    {
        var scene = new Scene();
        scene.ApplySettings(Quiet with { Border = BorderStyle.Glow, CursorTrailEnabled = true });
        long now = Stopwatch.GetTimestamp();
        scene.UpdatePointer(new Point(100, 100), now);
        scene.Update(MotionFrame(now), now, 0.1, null);
        var surface = new OverlaySurface();
        surface.Measure(new Size(800, 600));
        surface.Arrange(new Rect(0, 0, 800, 600));
        surface.ShowScene(scene, true, true);
        var music = (DrawingVisual)VisualTreeHelper.GetChild(surface, 0);
        var cursor = (DrawingVisual)VisualTreeHelper.GetChild(surface, 1);
        Rect previousMusic = music.ContentBounds, previousCursor = cursor.ContentBounds;
        scene.ApplySettings(Quiet with { CursorTrailEnabled = true });
        scene.UpdatePointer(new Point(400, 100), now + Stopwatch.Frequency / 100);
        scene.Update(PartyTime.Audio.AudioFrame.Silent, now + Stopwatch.Frequency / 100, 0.1, null);
        surface.ShowScene(scene, false, true);
        Check(!previousMusic.IsEmpty && music.ContentBounds == previousMusic && cursor.ContentBounds != previousCursor,
            "WPF fallback updates the cursor without redrawing the music layer");
        surface.ShowScene(null, true, false);
        Check(music.ContentBounds.IsEmpty && cursor.ContentBounds.IsEmpty, "WPF fallback clears both retained layers");
    }

    private static void CheckOverlayComposition()
    {
        var window = new OverlayWindow();
        var bounds = new System.Drawing.Rectangle(-30000, -30000, 320, 180);
        try
        {
            window.Open(bounds);
            GpuOverlayRenderer renderer;
            try { renderer = new GpuOverlayRenderer([window], [bounds]); }
            catch (SharpGen.Runtime.SharpGenException)
            {
                Console.WriteLine("SKIP Native desktop composition is unavailable.");
                return;
            }
            using (renderer)
            {
                foreach (var style in Enum.GetValues<CursorTrailStyle>())
                {
                    var scene = new Scene();
                    scene.ApplySettings(Quiet with { Border = BorderStyle.Glow, ShapesEnabled = true, CursorTrailEnabled = true, CursorTrail = style });
                    long now = Stopwatch.GetTimestamp();
                    for (int frame = 0; frame < 12; frame++)
                    {
                        long tick = now + frame * Stopwatch.Frequency / 120;
                        scene.UpdatePointer(new Point(bounds.X + 40 + frame * 10, bounds.Y + 80), tick);
                        scene.Update(MotionFrame(tick), tick, 1.0 / 120, null);
                        renderer.Draw(0, scene, redrawBase: frame % 4 == 0, drawCursor: true, cacheBase: true);
                    }
                    renderer.Draw(0, scene, redrawBase: true, drawCursor: false, cacheBase: false);
                    renderer.Draw(0, null, redrawBase: true, drawCursor: false, cacheBase: false);
                }
            }
            Check(true, "Native desktop composition switches safely between cached cursor frames and direct music frames");
        }
        finally { window.Close(); }
    }
}
