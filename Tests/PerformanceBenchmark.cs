using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using PartyTime.Audio;
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
        foreach (var blend in Enum.GetValues<ScreenSampleBlend>().Where(blend => blend <= ScreenSampleBlend.Negative))
            foreach (double zoom in new[] { 1.0, 1.04 })
                results.Add(Measure($"blend-{blend}-zoom-{zoom}", () => BeatScreenSampler.BlendPixels(background, held, output, width, height, blend, zoom), 400));

        var samples = new float[1200 * 2];
        for (int i = 0; i < samples.Length / 2; i++)
        {
            double t = i * Math.Tau / AudioAnalyzer.SampleRate;
            samples[i * 2] = (float)(0.17 * Math.Sin(100 * t) + 0.07 * Math.Sin(820 * t));
            samples[i * 2 + 1] = (float)(0.15 * Math.Sin(145 * t) + 0.05 * Math.Sin(6100 * t));
        }
        var analyzer = new AudioAnalyzer();
        long audioTime = Stopwatch.Frequency;
        long packetInterval = Stopwatch.Frequency / 40;
        results.Add(Measure("analyze-25ms", () =>
            analyzer.Push(MemoryMarshal.AsBytes(samples.AsSpan()), audioTime += packetInterval, FrequencyRange.Vocals), 2000));
        var silentAnalyzer = new AudioAnalyzer();
        Array.Clear(samples);
        results.Add(Measure("analyze-silence-25ms", () =>
            silentAnalyzer.Push(MemoryMarshal.AsBytes(samples.AsSpan()), audioTime += packetInterval, FrequencyRange.Vocals), 2000));

        var updateScene = new Scene();
        var frame = MotionFrame(Stopwatch.Frequency);
        results.Add(Measure("update-scene", () => updateScene.Update(frame, frame.Timestamp, 1.0 / 60, null), 10000));
        foreach (var preset in VisualPresets.All)
        {
            var scene = new Scene(); scene.ApplySettings(preset.Apply(new()));
            long now = Stopwatch.GetTimestamp();
            for (int i = 0; i < 80; i++) scene.UpdatePointer(new Point(100 + i * 8, 200 + Math.Sin(i / 5.0) * 60), now - (79 - i) * Stopwatch.Frequency / 200);
            for (int frameIndex = 0; frameIndex < 19; frameIndex++)
            {
                long timestamp = now + frameIndex * Stopwatch.Frequency / 10;
                scene.Update(MotionFrame(timestamp), timestamp, 0.1, null);
            }
            using var renderer = new SkiaSceneRenderer();
            results.Add(Measure("record-" + preset.Name, () => { using var picture = renderer.Record(scene, 1920, 1080, includeCursor: true); }, 2000));
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
        int warmups = Math.Clamp(count / 8, 50, 250);
        for (int i = 0; i < warmups; i++) action();
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
