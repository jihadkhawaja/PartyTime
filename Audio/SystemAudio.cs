using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;
using System.Threading.Tasks;
using System.IO;
using PartyTime.Interop;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PartyTime.Audio;

internal sealed class SystemAudio : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.UIThread;
    private CancellationTokenSource? _captureCancellation;
    private Process? _process;
    private bool _running;
    private int _captureGeneration;
    private readonly DispatcherTimer _poll;
    private WasapiRecorder? _recorder;
    private MMDevice? _device;
    private AudioFrame _latest = AudioFrame.Silent;
    private FrequencyRange _focus = FrequencyRange.Full;

    public SystemAudio()
    {
        _poll = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1.4)
        };
        _poll.Tick += (_, _) => Connect();
    }

    public AudioFrame Latest => Volatile.Read(ref _latest);
    public FrequencyRange Focus
    {
        get => Volatile.Read(ref _focus);
        set => Volatile.Write(ref _focus, value);
    }
    public event Action<string>? StatusChanged;

    public void Start()
    {
        if (_running) return;
        _running = true;
        Connect();
        _poll.Start();
    }

    public void Dispose() => Stop();

    public void Stop()
    {
        _running = false;
        _poll.Stop();
        Disconnect();
    }

    private void Connect()
    {
        if (!_running) return;
        if (!OperatingSystem.IsWindows()) { ConnectPortable(); return; }
        MMDevice? endpoint = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out endpoint))
            {
                Disconnect();
                StatusChanged?.Invoke("Waiting for a Windows audio output…");
                return;
            }

            if (_device?.ID == endpoint.ID)
                return;

            Disconnect();
            _device = endpoint;
            endpoint = null;
            _recorder = new WasapiRecorderBuilder()
                .WithDevice(_device)
                .WithLoopbackCapture()
                .WithEventSync()
                .WithBufferLength(25)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(AudioAnalyzer.SampleRate, 2))
                .Build();
            var analyzer = new AudioAnalyzer();
            int generation = _captureGeneration;
            _recorder.DataAvailable += (buffer, _, _, _) =>
            {
                var frame = analyzer.Push(buffer, Stopwatch.GetTimestamp(), Focus);
                if (frame != null && generation == Volatile.Read(ref _captureGeneration))
                    Volatile.Write(ref _latest, frame);
            };
            _recorder.RecordingStopped += OnRecordingStopped;
            _recorder.StartRecording();
            StatusChanged?.Invoke(_device.FriendlyName);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            Disconnect();
            StatusChanged?.Invoke("Audio output unavailable. Retrying…");
        }
        finally
        {
            endpoint?.Dispose();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        _dispatcher.Post(() =>
        {
            if (!ReferenceEquals(sender, _recorder))
                return;

            Debug.WriteLine(args.Exception);
            Disconnect();
            StatusChanged?.Invoke("Audio disconnected. Reconnecting…");
        });
    }

    private void Disconnect()
    {
        Interlocked.Increment(ref _captureGeneration);
        _captureCancellation?.Cancel();
        _captureCancellation?.Dispose();
        _captureCancellation = null;
        var process = _process;
        _process = null;
        if (process != null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
        if (OperatingSystem.IsWindows()) DisconnectWindows();
        Volatile.Write(ref _latest, AudioFrame.Silent);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void DisconnectWindows()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder != null)
        {
            recorder.RecordingStopped -= OnRecordingStopped;
            recorder.Dispose();
        }
        _device?.Dispose();
        _device = null;
        Volatile.Write(ref _latest, AudioFrame.Silent);
    }
    private void ConnectPortable()
    {
        if (_process is { HasExited: false }) return;
        Disconnect();
        try
        {
            var start = OperatingSystem.IsMacOS() ? MacCapture.CreateStartInfo("audio")
                : new ProcessStartInfo("parec");
            if (OperatingSystem.IsLinux())
                foreach (var arg in new[] { "--device=@DEFAULT_MONITOR@", "--format=float32le", "--rate=48000", "--channels=2", "--latency-msec=25" })
                    start.ArgumentList.Add(arg);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            var process = new Process { StartInfo = start };
            if (!process.Start()) throw new InvalidOperationException("Audio capture could not start.");
            _process = process;
            _captureCancellation = new CancellationTokenSource();
            _ = ReadAudio(process, _captureCancellation.Token);
            StatusChanged?.Invoke(OperatingSystem.IsMacOS() ? "System audio · allow Screen & System Audio Recording in macOS Settings"
                : "System audio · default PulseAudio / PipeWire output monitor");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Disconnect();
            StatusChanged?.Invoke(OperatingSystem.IsMacOS() ? ex.Message
                : "Audio capture needs parec (install pulseaudio-utils) and a running PulseAudio or PipeWire-Pulse server. Retrying…");
        }
    }

    private async Task ReadAudio(Process process, CancellationToken cancellation)
    {
        string? error = null;
        var stderr = process.StandardError.ReadToEndAsync(cancellation);
        try
        {
            var analyzer = new AudioAnalyzer();
            byte[] packet = new byte[512 * 2 * sizeof(float)];
            while (!cancellation.IsCancellationRequested)
            {
                await process.StandardOutput.BaseStream.ReadExactlyAsync(packet, cancellation);
                var frame = analyzer.Push(packet, Stopwatch.GetTimestamp(), Focus);
                if (frame != null && !cancellation.IsCancellationRequested && ReferenceEquals(Volatile.Read(ref _process), process))
                    Volatile.Write(ref _latest, frame);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            if (!cancellation.IsCancellationRequested)
            {
                try { error = (await stderr).Trim(); } catch (OperationCanceledException) { }
                error = string.IsNullOrEmpty(error) ? "Audio disconnected. Reconnecting…" : error;
            }
        }
        if (error != null) _dispatcher.Post(() => { if (ReferenceEquals(_process, process)) { Disconnect(); StatusChanged?.Invoke(error); } });
    }

}
