using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;
using PartyTime.Settings;
using PartyTime.Visualization;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Rectangle = System.Drawing.Rectangle;

internal static partial class Program
{
    private sealed record ScreenTiming(double AverageMs, double P95Ms, double MaxMs);
    private sealed record ScreenBenchmarkResult(string Effect, int DisplayWorkloads, bool RepeatedPrimaryDisplay, string[] CaptureBackends,
        int Frames, double FramesPerSecond, int MissedFrameBudgets, ScreenTiming CaptureBlendPublish,
        ScreenTiming UploadDrawSync, ScreenTiming TotalWork, double ProcessCpuPercent, double CpuCorePercent,
        long ManagedBytesPerFrame, int Gen0Collections, int Gen1Collections, int Gen2Collections,
        double PrivateMemoryStartMb, double PrivateMemoryEndMb);

    private static ScreenTiming Summarize(double[] times)
    {
        var sorted = times.Order().ToArray();
        return new(Math.Round(times.Average(), 3), Math.Round(sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1], 3),
            Math.Round(sorted[^1], 3));
    }

    private static void RunScreenBenchmark()
    {
#if DEBUG
        throw new InvalidOperationException("Run the screen benchmark with -c Release.");
#endif
        var screens = System.Windows.Forms.Screen.AllScreens;
        var primary = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        using var renderer = new ScreenBenchmarkRenderer(primary);
        var results = new List<ScreenBenchmarkResult>();
        var blends = new List<object>();
        var reportDirectory = Path.GetFullPath(Path.Combine(Output, "../benchmarks"));
        Directory.CreateDirectory(reportDirectory);

        // Isolate the CPU pixel loop using changing, deterministic image data at the capture cap.
        const int width = 960, height = 540, blendWarmup = 30, blendFrames = 120;
        var background = new byte[width * height * 4];
        var sample = new byte[background.Length];
        var output = new byte[background.Length];
        var random = new Random(1234);
        random.NextBytes(background);
        random.NextBytes(sample);
        foreach (var effect in Enum.GetValues<ScreenSampleBlend>())
        {
            var times = new double[blendFrames];
            long allocated = 0;
            for (int frame = -blendWarmup; frame < blendFrames; frame++)
            {
                if (frame == 0) allocated = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                BeatScreenSampler.BlendPixels(background, sample, output, width, height, effect, 1.03 - frame % 15 * 0.001);
                if (frame >= 0) times[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            var result = new { Effect = effect.ToString(), Width = width, Height = height, Frames = blendFrames,
                CpuBlend = Summarize(times), ManagedBytesPerFrame = bytes / blendFrames };
            blends.Add(result);
            Console.WriteLine("CPU blend " + JsonSerializer.Serialize(result));
        }

        foreach (var effect in Enum.GetValues<ScreenSampleBlend>())
            results.Add(MeasureScreenPipeline(effect, [primary], renderer, false));
        // Repeated primary capture also exercises fallback; this is not a physical multi-monitor measurement.
        results.Add(MeasureScreenPipeline(ScreenSampleBlend.Additive, [primary, primary], renderer, true));

        var idle = MeasureScreenIdle(primary);
        var metadata = new
        {
            RecordedAt = DateTimeOffset.UtcNow,
            Cpu = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "Unknown")?.ToString()?.Trim(),
            LogicalProcessors = Environment.ProcessorCount,
            Gpu = renderer.AdapterName,
            Os = RuntimeInformation.OSDescription,
            Runtime = RuntimeInformation.FrameworkDescription,
            Configuration = "Release",
            PrimaryDisplay = new { primary.Width, primary.Height },
            ConnectedDisplays = screens.Select(screen => new { screen.DeviceName, screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height }).ToArray(),
            TargetFps = 30, WarmupFrames = 15, MeasuredFrames = 90, BeatBpm = 120, CaptureCap = 960,
            Scope = "Screen layer only: real desktop capture, CPU blend, frozen bitmap publication, GPU upload and draw. Manual 30 FPS scheduling. A one-pixel readback waits for GPU completion; these are synchronized upload/draw times, not GPU utilization or compositor latency. Audio, controls, other effects and WPF fallback are excluded. Desktop content is uncontrolled; GPU capture reuses the last desktop frame when unchanged. Dual workloads repeat the primary display and capture sequentially; production capture workers run independently. Screenshots are never written to disk."
        };
        string jsonPath = Path.Combine(reportDirectory, "screen-benchmark.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(new { Environment = metadata, CpuBlend = blends, Pipelines = results, Idle = idle },
            new JsonSerializerOptions { WriteIndented = true }));
        var markdown = new StringBuilder();
        markdown.AppendLine("# Screen layer performance benchmark\n");
        markdown.AppendLine($"Recorded (UTC): {metadata.RecordedAt:u}. Release, {metadata.Runtime}, {metadata.Os}.\n");
        markdown.AppendLine($"CPU: {metadata.Cpu} ({metadata.LogicalProcessors} logical processors). GPU: {metadata.Gpu}. Primary display: {primary.Width} × {primary.Height}. Connected displays: {screens.Length}.\n");
        markdown.AppendLine(metadata.Scope + "\n");
        markdown.AppendLine("Settings: 120 BPM, new screenshot every 2 beats, 40% opacity, 3% zoom, 700 ms fade. Capture cap: 960 pixels on the longer side. Each case uses 15 warm-up frames and 90 measured frames at 30 FPS. CPU % is normalized over all logical processors; core % uses 100% for one fully occupied logical core.\n");
        markdown.AppendLine("| Effect | Display workloads | Capture backend | Capture + blend + publish avg / p95 ms | GPU upload + draw + sync avg / p95 ms | Total avg / p95 ms | FPS | Frames over 33.3 ms | CPU % | Managed bytes/frame |");
        markdown.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var result in results)
        {
            string Timing(ScreenTiming timing) => $"{timing.AverageMs:F3} / {timing.P95Ms:F3}";
            markdown.AppendLine($"| {result.Effect} | {result.DisplayWorkloads}{(result.RepeatedPrimaryDisplay ? " (repeated primary)" : "")} | {string.Join(" + ", result.CaptureBackends)} | {Timing(result.CaptureBlendPublish)} | {Timing(result.UploadDrawSync)} | {Timing(result.TotalWork)} | {result.FramesPerSecond:F2} | {result.MissedFrameBudgets} / {result.Frames} | {result.ProcessCpuPercent:F2} | {result.ManagedBytesPerFrame:N0} |");
        }
        markdown.AppendLine($"\nIdle (active timer, expired beat): {JsonSerializer.Serialize(idle)}.\n");
        markdown.AppendLine("Managed allocation counts exclude native WIC bitmap and GPU memory. Process private memory and GC counts are recorded per case in the JSON; short-run memory changes are not a leak test. Background activity, desktop content, display resolution, power settings and GPU load can affect results.\n");
        markdown.AppendLine("The repeated-primary case can use GDI for its second capture. Its backends are listed explicitly; treat it as fallback stress, not an actual two-monitor GPU measurement.\n");
        string baselinePath = Path.Combine(reportDirectory, "screen-benchmark-baseline.json");
        if (File.Exists(baselinePath))
        {
            using var baseline = JsonDocument.Parse(File.ReadAllText(baselinePath));
            var previous = baseline.RootElement.GetProperty("Pipelines").EnumerateArray()
                .Where(row => row.GetProperty("DisplayWorkloads").GetInt32() == 1)
                .ToDictionary(row => row.GetProperty("Effect").GetString()!);
            markdown.AppendLine("## Comparison with saved baseline\n");
            markdown.AppendLine("Single-display Screen pipeline. Baseline uses GDI capture and the original pixel loop; the current implementation uses the reported backend and precomputes zoom columns.\n");
            markdown.AppendLine("| Effect | Baseline avg work ms | Current avg work ms | Baseline FPS | Current FPS |");
            markdown.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var result in results.Where(row => row.DisplayWorkloads == 1))
                if (previous.TryGetValue(result.Effect, out var old))
                    markdown.AppendLine($"| {result.Effect} | {old.GetProperty("TotalWork").GetProperty("AverageMs").GetDouble():F3} | {result.TotalWork.AverageMs:F3} | {old.GetProperty("FramesPerSecond").GetDouble():F2} | {result.FramesPerSecond:F2} |");
            markdown.AppendLine("\nThis is a local before/after observation with uncontrolled desktop content, not a hardware-independent guarantee.\n");
        }
        markdown.AppendLine("Repeat with `dotnet run --project Tests/PartyTime.Tests.csproj -c Release -- --benchmark-screen`. Raw measurements and isolated CPU blend timings are in `screen-benchmark.json`.");
        File.WriteAllText(Path.Combine(reportDirectory, "screen-benchmark.md"), markdown.ToString());
        Console.WriteLine("Benchmark report: " + jsonPath);
    }

    private static ScreenBenchmarkResult MeasureScreenPipeline(ScreenSampleBlend effect, Rectangle[] bounds,
        ScreenBenchmarkRenderer renderer, bool repeatedPrimary)
    {
        const int warmup = 15, frames = 90;
        var samplers = bounds.Select(rect => new BeatScreenSampler(rect, automaticCapture: false)).ToArray();
        var captureTimes = new double[frames];
        var gpuTimes = new double[frames];
        var workTimes = new double[frames];
        var settings = new VisualizerSettings { ScreenSamplesEnabled = true, SampleBlend = effect, SampleBeats = 2 };
        using var process = Process.GetCurrentProcess();
        long period = Stopwatch.Frequency / 30;
        long firstBeat = Stopwatch.GetTimestamp();
        long schedule = firstBeat;
        long measureStart = 0, allocated = 0;
        TimeSpan cpuStart = default;
        int gen0 = 0, gen1 = 0, gen2 = 0;
        double privateStart = 0;
        try
        {
            for (int frame = -warmup; frame < frames; frame++)
            {
                long remaining = schedule - Stopwatch.GetTimestamp();
                if (remaining > 0) Thread.Sleep(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency));
                if (frame == 0)
                {
                    process.Refresh();
                    privateStart = process.PrivateMemorySize64 / 1048576.0;
                    cpuStart = process.TotalProcessorTime;
                    allocated = GC.GetTotalAllocatedBytes(precise: true);
                    gen0 = GC.CollectionCount(0); gen1 = GC.CollectionCount(1); gen2 = GC.CollectionCount(2);
                    measureStart = Stopwatch.GetTimestamp();
                }
                long start = Stopwatch.GetTimestamp();
                double seconds = Stopwatch.GetElapsedTime(firstBeat, start).TotalSeconds;
                long beat = firstBeat + (long)(Math.Floor(seconds * 2) / 2 * Stopwatch.Frequency);
                var request = new ScreenSampleRequest(settings, beat, (long)seconds + 1);
                double capture = 0, gpu = 0;
                for (int display = 0; display < samplers.Length; display++)
                {
                    long captureStart = Stopwatch.GetTimestamp();
                    samplers[display].Update(request);
                    if (!samplers[display].CaptureFrame())
                        throw new InvalidOperationException(samplers[display].Error ?? "Capture unexpectedly skipped.");
                    capture += Stopwatch.GetElapsedTime(captureStart).TotalMilliseconds;
                    long gpuStart = Stopwatch.GetTimestamp();
                    renderer.Draw(samplers[display].Latest!, request.Opacity(Stopwatch.GetTimestamp()));
                    gpu += Stopwatch.GetElapsedTime(gpuStart).TotalMilliseconds;
                }
                if (frame >= 0)
                {
                    captureTimes[frame] = capture;
                    gpuTimes[frame] = gpu;
                    workTimes[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }
                schedule += period;
                // Do not burst through missed deadlines; account for missed frame budgets below.
                if (schedule < Stopwatch.GetTimestamp()) schedule = Stopwatch.GetTimestamp();
            }
            long finalWait = schedule - Stopwatch.GetTimestamp();
            if (finalWait > 0) Thread.Sleep(TimeSpan.FromSeconds((double)finalWait / Stopwatch.Frequency));
            long end = Stopwatch.GetTimestamp();
            double wall = Stopwatch.GetElapsedTime(measureStart, end).TotalSeconds;
            long bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            process.Refresh();
            double coreCpu = (process.TotalProcessorTime - cpuStart).TotalSeconds / wall * 100;
            var result = new ScreenBenchmarkResult(effect.ToString(), bounds.Length, repeatedPrimary, samplers.Select(sampler => sampler.CaptureBackend).ToArray(), frames,
                Math.Round(frames / wall, 2), workTimes.Count(ms => ms > 1000.0 / 30), Summarize(captureTimes),
                Summarize(gpuTimes), Summarize(workTimes), Math.Round(coreCpu / Environment.ProcessorCount, 2),
                Math.Round(coreCpu, 2), bytes / frames, GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1,
                GC.CollectionCount(2) - gen2, Math.Round(privateStart, 2), Math.Round(process.PrivateMemorySize64 / 1048576.0, 2));
            Console.WriteLine("Pipeline " + JsonSerializer.Serialize(result));
            return result;
        }
        finally { foreach (var sampler in samplers) sampler.Dispose(); }
    }

    private static object MeasureScreenIdle(Rectangle bounds)
    {
        using var process = Process.GetCurrentProcess();
        using var sampler = new BeatScreenSampler(bounds);
        sampler.Update(new(new VisualizerSettings { ScreenSamplesEnabled = true }, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 5, 1));
        process.Refresh();
        var cpu = process.TotalProcessorTime;
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        long start = Stopwatch.GetTimestamp();
        Thread.Sleep(2000);
        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        process.Refresh();
        return new { DurationSeconds = Math.Round(seconds, 3), PublishedBitmap = sampler.Latest != null,
            ProcessCpuPercent = Math.Round((process.TotalProcessorTime - cpu).TotalSeconds / seconds / Environment.ProcessorCount * 100, 3),
            ManagedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated };
    }

    private sealed class ScreenBenchmarkRenderer : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID2D1Factory1 _factory;
        private readonly ID2D1Device _d2d;
        private readonly ID2D1DeviceContext _context;
        private readonly ID2D1Bitmap1 _target, _readback;
        private readonly GpuScreenSampleLayer _layer;
        private readonly Rectangle _bounds;
        public string AdapterName { get; }

        public ScreenBenchmarkRenderer(Rectangle bounds)
        {
            _bounds = bounds;
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null,
                out ID3D11Device? device, out Vortice.Direct3D.FeatureLevel _).CheckError();
            _device = device!;
            using var dxgi = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            AdapterName = adapter.Description.Description;
            _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
            _d2d = _factory.CreateDevice(dxgi);
            _context = _d2d.CreateDeviceContext(DeviceContextOptions.None);
            var format = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
            _target = _context.CreateBitmap(new SizeI(bounds.Width, bounds.Height), IntPtr.Zero, 0,
                new BitmapProperties1(format, 96, 96, BitmapOptions.Target));
            _readback = _context.CreateBitmap(new SizeI(1, 1), IntPtr.Zero, 0,
                new BitmapProperties1(format, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
            _layer = new GpuScreenSampleLayer(_context);
        }

        public void Draw(System.Windows.Media.Imaging.BitmapSource image, double opacity)
        {
            _context.Target = _target;
            _context.BeginDraw();
            _context.Clear(new Color4(0, 0, 0, 0));
            _layer.Draw(image, opacity, _bounds.Width, _bounds.Height);
            _context.EndDraw().CheckError();
            _context.Target = null;
            _readback.CopyFromBitmap(new System.Drawing.Point(0, 0), _target, new Rectangle(0, 0, 1, 1));
            _readback.Map(MapOptions.Read);
            _readback.Unmap();
        }

        public void Dispose()
        {
            _context.Target = null;
            _layer.Dispose(); _readback.Dispose(); _target.Dispose();
            _context.Dispose(); _d2d.Dispose(); _factory.Dispose(); _device.Dispose();
        }
    }
}
