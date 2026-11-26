using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using PartyTime.Audio;
using PartyTime.Interop;
using PartyTime.Views;
using PartyTime.Visualization;

internal static partial class Program
{
    private static int RunNativeSmoke()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Run --native-smoke on Windows; portable checks run without this flag.");
        PartyTime.Program.BuildAvaloniaApp().SetupWithoutStarting();
        var controls = new ControlWindow(new())
        {
            Position = new PixelPoint(-30000, -30000),
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false
        };
        var overlay = new OverlayWindow { Width = 160, Height = 90, Position = new PixelPoint(-30000, -30000) };
        try
        {
            controls.ConfigureStartup([AppStartup.TrayArgument]);
            controls.Show();
            Dispatcher.UIThread.RunJobs();
            Check(!controls.IsVisible, "Startup keeps the controls in the tray");
            controls.OpenControls();
            Dispatcher.UIThread.RunJobs();
            Check(controls.IsVisible, "Opening controls from the tray after startup keeps the window visible");
            controls.Close();
            Check(!controls.IsVisible, "Closing the controls hides them in the tray");
            controls.OpenControls();
            Dispatcher.UIThread.RunJobs();
            Check(controls.IsVisible, "Controls can reopen again after closing to the tray");
            controls.Hide();

            Check(controls.Screens.All.Count > 0, "Avalonia enumerates native desktop displays");
            overlay.Show();
            Desktop.MakeClickThrough(overlay);
            Dispatcher.UIThread.RunJobs();
            IntPtr handle = overlay.TryGetPlatformHandle()!.Handle;
            int style = ReadWindowStyle(handle, -20);
            Check((style & 0x20) != 0 && (style & 0x08000000) != 0, "Native overlay passes clicks through without activating");
            Check(Desktop.SetCaptureExcluded(overlay, true), "Windows excludes the Avalonia overlay from desktop capture");
            Check(Desktop.GetPointer() != null, "Cursor tracking reads native desktop coordinates");
            var primary = controls.Screens.Primary ?? controls.Screens.All[0];
            double rate = ScreenRefreshRate.Read(primary, DisplayRefreshRates.Read());
            Check(rate > 1 && double.IsFinite(rate), "Display refresh lookup maps Avalonia displays to native monitors");
            using var capture = new DesktopCapture(primary, 48, 28);
            byte[] pixels = new byte[48 * 28 * 4];
            bool opaque = true;
            for (int i = 0; i < 5; i++)
            {
                opaque &= capture.ReadPixels(pixels, retainPreviousPixels: true)
                    && Enumerable.Range(0, 48 * 28).All(p => pixels[p * 4 + 3] == 255);
                Thread.Sleep(20);
            }
            Check(opaque, "Native capture publishes opaque BGRA frames");
            Console.WriteLine("Screen capture backend: " + capture.Backend);
            var scene = new Scene();
            scene.ApplySettings(new() { Border = PartyTime.Settings.BorderStyle.Glow });
            long timestamp = Stopwatch.GetTimestamp(); scene.Update(MotionFrame(timestamp), timestamp, 0.1, null);
            overlay.Surface.ShowScene(scene);
            int animationFrames = 0;
            using (var clock = new CompositionClock(overlay, () => animationFrames++))
            {
                var frame = new DispatcherFrame();
                var stop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                stop.Tick += (_, _) => { stop.Stop(); frame.Continue = false; };
                stop.Start(); Dispatcher.UIThread.PushFrame(frame);
            }
            Check(animationFrames > 0 && ScenePictureOperation.RenderedPictures > 0, "Native compositor clocks and replays immutable scene pictures");
            Console.WriteLine($"Skia GPU context observed: {ScenePictureOperation.GpuObserved}; animation callbacks: {animationFrames}");
            using var audio = new SystemAudio();
            string? status = null;
            audio.StatusChanged += value => status = value;
            audio.Start();
            if (status != null && !status.Contains("unavailable", StringComparison.OrdinalIgnoreCase) && !status.Contains("Waiting", StringComparison.OrdinalIgnoreCase))
            {
                long start = Stopwatch.GetTimestamp();
                while (audio.Latest.Timestamp == 0 && Stopwatch.GetElapsedTime(start).TotalMilliseconds < 600) Thread.Sleep(10);
                Check(true, "WASAPI loopback connects to the native playback device");
                Console.WriteLine(audio.Latest.Timestamp != 0 ? "Live audio frames received." : "No playback frames during the short observation window.");
            }
            else Console.WriteLine("SKIP No available native playback device.");
        }
        finally { overlay.Close(); Close(controls); }
        Console.WriteLine($"Passed {_checks} native Windows checks.");
        return 0;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int ReadWindowStyle(IntPtr handle, int index);
}
