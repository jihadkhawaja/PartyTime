using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PartyTime.Audio;

internal sealed class SystemAudio : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _poll;
    private WasapiRecorder? _recorder;
    private MMDevice? _device;
    private AudioFrame _latest = AudioFrame.Silent;
    private FrequencyRange _focus = FrequencyRange.Full;

    public SystemAudio(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
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
        Connect();
        _poll.Start();
    }

    public void Dispose()
    {
        _poll.Stop();
        Disconnect();
    }

    private void Connect()
    {
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
            _recorder.DataAvailable += (buffer, _, _, _) =>
            {
                var frame = analyzer.Push(buffer, Stopwatch.GetTimestamp(), Focus);
                if (frame != null)
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
        _dispatcher.BeginInvoke(() =>
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
}
