using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
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
using Rect = System.Windows.Rect;

internal static partial class Program
{
    private static void CheckScreenSamples()
    {
        var defaults = VisualizerSettings.ReadSettings("{}");
        Check(!defaults.ScreenSamplesEnabled && defaults.SampleReaction == AudioReaction.Low,
            "Existing settings keep screen capture disabled with bass as its default beat source");
        var invalid = VisualizerSettings.ReadSettings("""
            {"SampleBlend":999,"SampleReaction":999,"SampleBeats":0,"SampleOpacity":190,"SampleZoom":-4,"SampleFade":5000}
            """);
        Check(invalid.SampleBlend == ScreenSampleBlend.Additive && invalid.SampleReaction == AudioReaction.Focus
            && invalid.SampleBeats == 1 && invalid.SampleOpacity == 100 && invalid.SampleZoom == 0 && invalid.SampleFade == 2000,
            "Screen sample settings validate enums and clamp persisted limits");
        var settings = Quiet with { ScreenSamplesEnabled = true, SampleReaction = AudioReaction.Full,
            SampleBeats = 2, SampleOpacity = 60, SampleZoom = 4, SampleFade = 700 };
        Check(VisualizerSettings.ReadSettings(JsonSerializer.Serialize(settings)) == settings,
            "Screen sample preferences round-trip without persisting screenshot data");
        Check(VisualPresets.All.All(preset => preset.Apply(settings).ScreenSamplesEnabled
            && preset.Apply(settings).SampleBeats == 2 && preset.Apply(settings).SampleOpacity == 60),
            "Visual presets preserve screen sample preferences");

        long now = Stopwatch.GetTimestamp();
        long Tick(double seconds) => now + (long)(seconds * Stopwatch.Frequency);
        var playback = new ScreenSamplePlayback();
        playback.ApplySettings(settings);
        long initial = playback.Request.Generation;
        playback.Update(MotionFrame(now), now);
        long first = playback.Request.Generation;
        Check(first == initial + 1 && playback.Request.Opacity(now) > 0,
            "The first fresh beat captures and shows a sample even with every other visual disabled");
        playback.Update(MotionFrame(now), Tick(0.02));
        Check(playback.Request.Generation == first, "Repeated render frames never count the same beat twice");
        playback.Update(MotionFrame(Tick(0.5)), Tick(0.5));
        Check(playback.Request.Generation == first && playback.Request.Opacity(Tick(0.5)) > 0.3,
            "The held screenshot pulses again on an intermediate beat");
        playback.Update(MotionFrame(Tick(1)), Tick(1));
        Check(playback.Request.Generation == first + 1, "Every two beats jumps to the next screenshot");
        var request = playback.Request;
        Check(request.Opacity(Tick(1.2)) < request.Opacity(Tick(1)) && request.Opacity(Tick(2)) == 0,
            "Screen sample opacity decays and clears completely during silence");
        playback.Update(MotionFrame(now), Tick(2));
        Check(playback.Request == request, "Stale audio cannot retrigger old screen samples");
        playback.Update(MotionFrame(Tick(3)), Tick(2));
        Check(playback.Request == request, "Future audio timestamps cannot trigger a screenshot");
        playback.ApplySettings(settings with { SampleReaction = AudioReaction.High });
        var lowOnly = MotionFrame(Tick(3)) with { High = AudioSignal.Silent };
        long before = playback.Request.Generation;
        playback.Update(lowOnly, Tick(3));
        Check(playback.Request.Generation == before && playback.Request.Opacity(Tick(3)) == 0,
            "The sample beat source remains independent of other frequency ranges");
        playback.ApplySettings(settings with { SampleReaction = AudioReaction.Focus, Focus = FrequencyFocus.Bass });
        playback.Update(MotionFrame(Tick(4)), Tick(4));
        Check(playback.Request.Opacity(Tick(4)) == 0, "Follow focus rejects audio from an outdated frequency focus");
        playback.ApplySettings(settings with { ScreenSamplesEnabled = false });
        playback.Update(MotionFrame(Tick(5)), Tick(5));
        Check(playback.Request.Opacity(Tick(5)) == 0, "Disabling screen samples clears their visibility immediately");

        foreach (var (effect, expected) in new[]
        {
            (ScreenSampleBlend.Normal, 160), (ScreenSampleBlend.Additive, 255), (ScreenSampleBlend.Screen, 198),
            (ScreenSampleBlend.Multiply, 64), (ScreenSampleBlend.Difference, 58), (ScreenSampleBlend.Negative, 95)
        })
            Check(BeatScreenSampler.BlendChannel(102, 160, effect) == expected, $"{effect} uses the expected channel blend");
        byte[] background = [102, 102, 102, 0, 10, 20, 30, 0];
        byte[] held = [160, 160, 160, 0, 90, 80, 70, 0];
        byte[] pixels = new byte[8];
        BeatScreenSampler.BlendPixels(background, held, pixels, 2, 1, ScreenSampleBlend.Difference, 1);
        Check(pixels.SequenceEqual(new byte[] { 58, 58, 58, 255, 80, 60, 40, 255 }),
            "Blending combines the held sample with the current desktop using opaque BGRA pixels");
        byte[] zoomSample = Enumerable.Range(1, 5).SelectMany(value => new byte[] { (byte)(value * 10), 0, 0, 255 }).ToArray();
        byte[] zoomOutput = new byte[zoomSample.Length];
        BeatScreenSampler.BlendPixels(new byte[zoomSample.Length], zoomSample, zoomOutput, 5, 1, ScreenSampleBlend.Normal, 2);
        Check(Enumerable.Range(0, 5).Select(index => zoomOutput[index * 4]).SequenceEqual(new byte[] { 20, 20, 30, 30, 40 }),
            "Beat zoom crops the held screenshot around its center without reading outside the image");
        var image = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Pbgra32, null, pixels, 8);
        image.Freeze();
        var surface = new OverlaySurface();
        surface.Measure(new System.Windows.Size(200, 100));
        surface.Arrange(new Rect(0, 0, 200, 100));
        surface.ShowScene(null, true, false, image, 0.5);
        var rendered = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(surface);
        byte[] pixel = new byte[4];
        rendered.CopyPixels(new Int32Rect(50, 50, 1, 1), pixel, 4, 0);
        Check(pixel[3] is >= 126 and <= 129 && pixel[0] is >= 28 and <= 30,
            "The WPF fallback renders samples independently with premultiplied opacity");
        surface.ShowScene(null, true, false);
        Check(((DrawingVisual)VisualTreeHelper.GetChild(surface, 0)).ContentBounds.IsEmpty,
            "The WPF fallback clears the sample layer after fade or disable");
        CheckSampleGpu(image);

        using var sampler = new BeatScreenSampler(new System.Drawing.Rectangle(0, 0, 64, 48));
        Check(sampler.Latest == null, "Screen capture waits for a beat instead of capturing when constructed");
        sampler.Update(new(settings with { SampleFade = 2000 }, Stopwatch.GetTimestamp(), 1));
        Check(SpinWait.SpinUntil(() => sampler.Latest != null || sampler.Error != null, 2500),
            "The background capture worker completes without blocking the render thread");
        if (sampler.Error != null) Console.WriteLine("SKIP Native desktop capture unavailable: " + sampler.Error);
        else Check(sampler.Latest is { IsFrozen: true, PixelWidth: 64, PixelHeight: 48 },
            "Native desktop capture publishes a frozen bitmap across threads");
        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        using var fullScreen = new BeatScreenSampler(bounds, automaticCapture: false);
        var captureRequest = new ScreenSampleRequest(settings with { SampleFade = 2000 }, Stopwatch.GetTimestamp(), 1);
        fullScreen.Update(captureRequest);
        Check(fullScreen.CaptureFrame() && fullScreen.Latest is { IsFrozen: true } captured
            && Math.Max(captured.PixelWidth, captured.PixelHeight) <= 960,
            "Full-display capture uses the available backend and honors the image size cap");
        Console.WriteLine("Screen capture backend: " + fullScreen.CaptureBackend);
        Check(fullScreen.CaptureFrame() && fullScreen.Latest != null,
            "An unchanged desktop keeps a valid sample without requiring another desktop update");
        var last = fullScreen.Latest;
        fullScreen.Update(captureRequest with { BeatTimestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 5 });
        Check(!fullScreen.CaptureFrame() && ReferenceEquals(fullScreen.Latest, last),
            "An expired beat skips desktop capture and bitmap publication entirely");
        fullScreen.Dispose();
        Check(!fullScreen.CaptureFrame() && fullScreen.Latest == null,
            "Disposed capture workers release their screenshot and reject subsequent frames");
    }

    private static unsafe void CheckSampleGpu(BitmapSource image)
    {
        D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, null,
            out ID3D11Device? device, out Vortice.Direct3D.FeatureLevel _).CheckError();
        using var gpu = device!;
        using var dxgi = gpu.QueryInterface<IDXGIDevice>();
        using var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        using var d2d = factory.CreateDevice(dxgi);
        using var context = d2d.CreateDeviceContext(DeviceContextOptions.None);
        var format = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        using var target = context.CreateBitmap(new SizeI(200, 100), IntPtr.Zero, 0,
            new BitmapProperties1(format, 96, 96, BitmapOptions.Target));
        using var readback = context.CreateBitmap(new SizeI(200, 100), IntPtr.Zero, 0,
            new BitmapProperties1(format, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        using var layer = new GpuScreenSampleLayer(context);
        for (int frame = 0; frame < 3; frame++)
        {
            var source = frame == 1 ? image.Clone() : image;
            context.Target = target;
            context.BeginDraw();
            context.Clear(new Color4(0, 0, 0, 0));
            layer.Draw(frame == 2 ? null : source, 0.5, 200, 100);
            context.EndDraw().CheckError();
            context.Target = null;
            readback.CopyFromBitmap(target);
            var mapped = readback.Map(MapOptions.Read);
            try
            {
                byte alpha = ((byte*)mapped.Bits)[50 * mapped.Pitch + 50 * 4 + 3];
                Check(frame == 2 ? alpha == 0 : alpha is >= 126 and <= 129,
                    frame == 2 ? "Direct2D clears and releases disabled screen samples"
                        : "Direct2D uploads changing sample frames and preserves overlay opacity");
            }
            finally { readback.Unmap(); }
        }
    }

    private static void CheckSampleControls()
    {
        var window = new ControlWindow(Quiet, persistSettings: false)
        {
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000, Top = -30000
        };
        T Control<T>(string name) => (T)window.FindName(name);
        VisualizerSettings Current() => (VisualizerSettings)typeof(ControlWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        try
        {
            window.Show();
            var tabs = FindTabs((DependencyObject)window.Content);
            tabs.SelectedItem = tabs.Items.Cast<TabItem>().First(tab => tab.Header is "Screen");
            Check(!Current().ScreenSamplesEnabled && Control<ComboBox>("SampleBlendPicker").Items.Count == 6,
                "The Screen tab initializes disabled with all six overlay effects");
            Control<CheckBox>("ScreenSamplesToggle").IsChecked = true;
            Control<ComboBox>("SampleBlendPicker").SelectedIndex = (int)ScreenSampleBlend.Negative;
            Control<ComboBox>("SampleReactionPicker").SelectedIndex = (int)AudioReaction.Mid;
            Control<Slider>("SampleBeatsSlider").Value = 4;
            Control<Slider>("SampleOpacitySlider").Value = 55;
            Control<Slider>("SampleZoomSlider").Value = 6;
            Control<Slider>("SampleFadeSlider").Value = 950;
            Check(Current().ScreenSamplesEnabled && Current().SampleBlend == ScreenSampleBlend.Negative
                && Current().SampleReaction == AudioReaction.Mid && Current().SampleBeats == 4
                && Current().SampleOpacity == 55 && Current().SampleZoom == 6 && Current().SampleFade == 950,
                "Editing sample controls applies independent blend, timing, opacity, zoom, and frequency settings");
            Check(Control<TextBlock>("SampleStatus").Text.StartsWith("Start the party")
                && Control<TextBlock>("SampleBeatsLabel").Text.Contains("4 beats"),
                "Sample descriptions explain when capture begins and how many beats each image lasts");
            foreach (int width in new[] { 584, 510 })
            {
                window.Width = width;
                window.Measure(new System.Windows.Size(width, 744));
                window.Arrange(new Rect(0, 0, width, 744));
                window.UpdateLayout();
                var target = new RenderTargetBitmap(width, 744, 96, 96, PixelFormats.Pbgra32);
                target.Render((Visual)window.Content);
                SavePng(target, Path.Combine(Output, $"controls-Screen-{width}.png"));
                ((ScrollViewer)((TabItem)tabs.SelectedItem).Content).ScrollToEnd();
                window.UpdateLayout();
                target = new RenderTargetBitmap(width, 744, 96, 96, PixelFormats.Pbgra32);
                target.Render((Visual)window.Content);
                SavePng(target, Path.Combine(Output, $"controls-Screen-{width}-bottom.png"));
            }
            Control<CheckBox>("ScreenSamplesToggle").IsChecked = false;
            Check(Control<FrameworkElement>("ScreenSamplesPanel").Visibility == Visibility.Collapsed
                && Control<System.Windows.Controls.Image>("SamplePreview").Source == null,
                "Disabling screen samples hides their controls and clears the live preview");
        }
        finally
        {
            ((System.Windows.Threading.DispatcherTimer)typeof(ControlWindow).GetField("_saveTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Stop();
            window.Hide();
        }
    }
}
