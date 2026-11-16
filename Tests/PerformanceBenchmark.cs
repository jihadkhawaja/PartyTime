using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using PartyTime.Settings;
using PartyTime.Visualization;

internal static partial class Program
{
    private static int RunPerformanceBenchmark(string[] args)
    {
        AppBuilder.Configure<PartyTime.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
        var results = new System.Collections.Generic.List<object>();
        const int width = 960, height = 540;
        var background = new byte[width * height * 4];
        var held = new byte[background.Length];
        var output = new byte[background.Length];
        new Random(42).NextBytes(background); new Random(83).NextBytes(held);
        foreach (var blend in Enum.GetValues<ScreenSampleBlend>())
            foreach (double zoom in new[] { 1.0, 1.04 })
                results.Add(Measure($"blend-{blend}-zoom-{zoom}", () => BeatScreenSampler.BlendPixels(background, held, output, width, height, blend, zoom), 80));
        foreach (var preset in VisualPresets.All)
        {
            var scene = new Scene(); scene.ApplySettings(preset.Apply(new()));
            long now = Stopwatch.GetTimestamp();
            for (int i = 0; i < 80; i++) scene.UpdatePointer(new Point(100 + i * 8, 200 + Math.Sin(i / 5.0) * 60), now - (79 - i) * Stopwatch.Frequency / 200);
            scene.Update(MotionFrame(now), now, 0.1, null);
            using var renderer = new SkiaSceneRenderer();
            results.Add(Measure("record-" + preset.Name, () => { using var picture = renderer.Record(scene, 1920, 1080, includeCursor: true); }, 100));
        }
        string directory = Path.GetFullPath(Path.Combine(Output, "..", "benchmarks")); Directory.CreateDirectory(directory);
        string label = args.SkipWhile(a => a != "--label").Skip(1).FirstOrDefault() ?? "latest";
        string path = Path.Combine(directory, "performance-" + Path.GetFileName(label) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(path); return 0;
    }

    private static object Measure(string name, Action action, int count)
    {
        for (int i = 0; i < 15; i++) action();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        double[] durations = new double[count];
        for (int i = 0; i < count; i++) { long start = Stopwatch.GetTimestamp(); action(); durations[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Array.Sort(durations);
        double mean = durations.Average(), p95 = durations[(int)(count * 0.95)];
        Console.WriteLine($"{name}: {mean:F3} ms mean / {p95:F3} ms p95 / {allocated / count:N0} bytes");
        return new { name, meanMs = mean, p95Ms = p95, bytesPerFrame = allocated / count };
    }
}
