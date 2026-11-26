using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Avalonia;
using PartyTime.Views;
using PartyTime.Visualization;

internal static partial class Program
{
    private static int RunNativePerformanceBenchmark()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Native capture comparison requires Windows.");
        AppBuilder.Configure<PartyTime.App>().UsePlatformDetect().SetupWithoutStarting();
        var window = new ControlWindow(new(), persistSettings: false);
        var results = new System.Collections.Generic.List<object>();
        try
        {
            foreach (var screen in window.Screens.All)
                foreach (bool gpu in new[] { false, true })
                {
                    double scale = Math.Min(1, 960.0 / Math.Max(screen.Bounds.Width, screen.Bounds.Height));
                    int width = (int)Math.Round(screen.Bounds.Width * scale), height = (int)Math.Round(screen.Bounds.Height * scale);
                    using var capture = new DesktopCapture(screen, width, height, preferGpu: gpu);
                    byte[] pixels = new byte[width * height * 4];
                    for (int i = 0; i < 6; i++) { capture.ReadPixels(pixels, retainPreviousPixels: true); Thread.Sleep(20); }
                    const int count = 60;
                    double[] work = new double[count], cadence = new double[count - 1];
                    long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp(), previous = start;
                    int updated = 0, available = 0;
                    for (int i = 0; i < count; i++)
                    {
                        long due = start + i * Stopwatch.Frequency / 30;
                        double wait = (due - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
                        if (wait > 1) Thread.Sleep((int)Math.Ceiling(wait));
                        long before = Stopwatch.GetTimestamp(), version = capture.Version;
                        if (i > 0) cadence[i - 1] = Stopwatch.GetElapsedTime(previous, before).TotalMilliseconds;
                        previous = before;
                        if (capture.ReadPixels(pixels, retainPreviousPixels: true)) available++;
                        if (version != capture.Version) updated++;
                        work[i] = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
                    }
                    allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    Array.Sort(work);
                    var result = new { requested = gpu ? "GPU preferred" : "GDI reference", backend = capture.Backend,
                        width, height, meanMs = work.Average(), p95Ms = work[(int)(count * 0.95)], bytesPerAttempt = allocated / count,
                        available, updated, cadenceMeanMs = cadence.Average(), cadenceP95Ms = cadence.Order().ElementAt((int)(cadence.Length * 0.95)) };
                    results.Add(result); Console.WriteLine(JsonSerializer.Serialize(result));
                }
        }
        finally { Close(window); }
        string directory = Path.GetFullPath(Path.Combine(Output, "..", "benchmarks")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "capture-performance.json"), JsonSerializer.Serialize(new
        {
            note = "Live desktop content is uncontrolled. Timings measure capture/readback work at scheduled 30 Hz, not end-to-end overlay latency or GPU utilization.", results
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
