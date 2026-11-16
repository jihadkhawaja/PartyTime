using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PartyTime.Audio;
using PartyTime.Settings;
using PartyTime.Views;
using PartyTime.Visualization;

internal static partial class Program
{
    private sealed class DrawingControl(Action<DrawingContext> draw) : Control
    {
        public override void Render(DrawingContext context) => draw(context);
    }

    private static RenderTargetBitmap Render(Control control, int width, int height)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        control.UpdateLayout();
        var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        target.Render(control);
        return target;
    }

    private static void CheckTransparency(string path)
    {
        var bitmap = OverlayImageStore.Load(path)!;
        Check(bitmap.PixelSize == new PixelSize(8, 4), "Import preserves small image dimensions");
        var visual = new DrawingControl(context => new AvaloniaSceneDrawing(context).DrawImage(bitmap, new Rect(0, 0, 80, 40), 0.5));
        using var rendered = Render(visual, 80, 40);
        var pixels = BitmapPixels.Read(rendered);
        Check(pixels[3] == 0 && Math.Abs(pixels[(20 * 80 + 40) * 4 + 3] - 64) <= 1,
            "Avalonia preserves transparent pixels and combines image alpha with opacity");
        SavePng(rendered, Path.Combine(Output, "avalonia-alpha.png"));

        byte[] samplePixels = [58, 58, 58, 255, 80, 60, 40, 255];
        using var sample = BitmapPixels.Create(samplePixels, 2, 1);
        var surface = new OverlaySurface();
        surface.ShowScene(null, true, false, sample, 0.5);
        using var screen = Render(surface, 200, 100);
        pixels = BitmapPixels.Read(screen);
        int index = (50 * 200 + 50) * 4;
        Check(pixels[index + 3] is >= 126 and <= 129 && pixels[index] is >= 28 and <= 30,
            "Avalonia renders screen samples independently with premultiplied opacity");
        surface.ShowScene(null, true, false);
        using var cleared = Render(surface, 200, 100);
        Check(BitmapPixels.Read(cleared).All(value => value == 0), "Stopping samples clears the overlay to transparent");
    }

    private static void CheckPresets()
    {
        var current = new VisualizerSettings { Focus = FrequencyFocus.Custom, LowHz = 100, HighHz = 900,
            Response = 135, FrameRate = OverlayFrameRate.Fps60, Images = [new() { Path = "keep.png" }] };
        Check(VisualPresets.All.Length >= 8 && VisualPresets.All.Select(p => p.Name).Distinct().Count() == VisualPresets.All.Length,
            "The preset library contains eight distinct looks");
        var gallery = new DrawingControl(context =>
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 14, 25)), null, new Rect(0, 0, 800, 960));
            for (int i = 0; i < VisualPresets.All.Length; i++)
            {
                using var translation = context.PushTransform(Matrix.CreateTranslation(i % 2 * 400, i / 2 * 240));
                context.DrawText(new FormattedText(VisualPresets.All[i].Name, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 15, Brushes.White), new Point(12, 4));
                using var content = context.PushTransform(Matrix.CreateTranslation(0, 28));
                using var clip = context.PushClip(new Rect(0, 0, 400, 208));
                var scene = new Scene();
                scene.ApplySettings(VisualPresets.All[i].Apply(new()));
                long now = Stopwatch.GetTimestamp();
                for (int p = 0; p < 18; p++) scene.UpdatePointer(new Point(70 + p * 13, 105 + Math.Sin(p * 0.3) * 35), now - (17 - p) * Stopwatch.Frequency / 50);
                scene.Update(MotionFrame(now), now, 0.1, null);
                scene.Draw(context, 400, 208);
            }
        });
        foreach (var preset in VisualPresets.All)
        {
            var applied = preset.Apply(current);
            Check(ReferenceEquals(applied.Images, current.Images) && applied.Focus == current.Focus && applied.LowHz == 100
                && applied.HighHz == 900 && applied.Response == 135 && applied.FrameRate == current.FrameRate,
                $"{preset.Name} preserves images, audio focus, response, and frame rate");
        }
        using var target = Render(gallery, 800, 960);
        Check(BitmapPixels.Read(target).Any(value => value != 0), "Every preset renders through Avalonia and Skia");
        SavePng(target, Path.Combine(Output, "preset-gallery.png"));
    }

    private static VisualizerSettings Current(ControlWindow window) => (VisualizerSettings)typeof(ControlWindow)
        .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void Close(ControlWindow window)
    {
        typeof(ControlWindow).GetField("_exiting", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void CheckControls(string imagePath)
    {
        var settings = ImageSettings(imagePath) with { ShapesEnabled = true, ScreenSamplesEnabled = true };
        var window = new ControlWindow(settings, persistSettings: false);
        try
        {
            Check(Current(window).Images.SequenceEqual(settings.Images), "Avalonia controls initialize without altering saved settings");
            window.FindControl<CheckBox>("CursorTrailToggle")!.IsChecked = true;
            window.FindControl<ComboBox>("CursorStylePicker")!.SelectedIndex = (int)CursorTrailStyle.Shapes;
            window.FindControl<ComboBox>("ShapeMotionPicker")!.SelectedIndex = (int)ShapeMotion.Spiral;
            Check(Current(window).CursorTrailEnabled && Current(window).CursorTrail == CursorTrailStyle.Shapes
                && Current(window).ShapeMotion == ShapeMotion.Spiral, "Motion controls update independent layers");
            window.FindControl<ComboBox>("PresetPicker")!.SelectedIndex = 7;
            Check(Current(window).ShapesEnabled, "Selecting a preset waits for Apply preset");
            window.FindControl<Button>("ApplyPresetButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!Current(window).ShapesEnabled && Current(window).CursorTrailEnabled && Current(window).Images.SequenceEqual(settings.Images),
                "Applying a preset updates controls and preserves image customization");
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
            foreach (var tab in tabs.Items.Cast<TabItem>())
            {
                tabs.SelectedItem = tab;
                Dispatcher.UIThread.RunJobs();
                using var target = Render((Control)window.Content!, 584, 744);
                SavePng(target, Path.Combine(Output, $"controls-{tab.Header}.png"));
                Check(target.PixelSize == new PixelSize(584, 744), $"{tab.Header} tab renders with Avalonia");
            }
            window.Hide();
            var preview = window.FindControl<PartyTime.Controls.StylePreview>("Preview")!;
            Check(typeof(PartyTime.Controls.StylePreview).GetField("_clock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview) == null,
                "Hiding controls stops the animated style preview clock");
            Check(typeof(ControlWindow).GetField("_samplePreviewClock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) == null,
                "Hiding controls stops the screen preview clock");
        }
        finally { Close(window); }
    }

    private static void CheckBulkControls(string imagePath)
    {
        OverlayImageSettings[] images = [new() { Name = "One", Path = imagePath, Size = 12, Opacity = 30, ChromaKeyEnabled = true },
            new() { Name = "Two", Path = imagePath, Size = 24, Opacity = 80, Enabled = false },
            new() { Name = "Three", Path = imagePath, Opacity = 90, Reaction = AudioReaction.Mid }];
        var window = new ControlWindow(ImageSettings(imagePath) with { Images = images }, persistSettings: false);
        T C<T>(string name) where T : Control => window.FindControl<T>(name)!;
        try
        {
            var list = C<ListBox>("ImageList");
            list.SelectedItems!.Clear();
            list.SelectedItems.Add(images[0]); list.SelectedItems.Add(images[1]);
            Check(C<CheckBox>("ImageEnabled").IsChecked == null && C<TextBlock>("ImageSizeLabel").Text!.Contains("Mixed"),
                "Mixed image values are identified across the selection");
            C<Slider>("ImageSize").Value = 32;
            Check(Current(window).Images[0] == images[0] with { Size = 32 } && Current(window).Images[1] == images[1] with { Size = 32 }
                && Current(window).Images[2] == images[2], "Bulk size edits preserve other fields and unselected images");
            C<ComboBox>("ImageReaction").SelectedIndex = (int)AudioReaction.Full;
            Check(Current(window).Images.Take(2).All(i => i.Reaction == AudioReaction.Full) && Current(window).Images[2].Reaction == AudioReaction.Mid,
                "Frequency routing applies only to selected images");
            C<CheckBox>("ImageEnabled").IsChecked = true;
            Check(Current(window).Images.All(i => i.Enabled), "Bulk visibility edits enable selected images");
            C<Button>("SelectAllImagesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(list.SelectedItems.Count == 3, "Select all selects the image library");
            C<Button>("RemoveImageButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Current(window).Images.Length == 0 && !C<Button>("SelectAllImagesButton").IsEnabled && !C<Control>("ImageEditor").IsVisible,
                "Removing selected images clears the library and hides its editor");
        }
        finally { Close(window); }
    }
}
