using System;
using System.Diagnostics;
using PartyTime.Audio;
using PartyTime.Settings;

namespace PartyTime.Visualization;

internal sealed record ScreenSampleRequest(VisualizerSettings Settings, long BeatTimestamp, long Generation)
{
    public double Pulse(long now) => Math.Exp(-Math.Max(0, Stopwatch.GetElapsedTime(BeatTimestamp, now).TotalSeconds) / 0.16);
    public double Zoom(long now) => 1 + Settings.SampleZoom / 100.0 * Pulse(now);
    public double LiveZoom(long now) => Settings.SampleBlend == ScreenSampleBlend.ZoomOnly ? Zoom(now) : 1;

    public double Opacity(long now)
    {
        if (!Settings.ScreenSamplesEnabled || BeatTimestamp == 0
            || Settings.SampleBlend == ScreenSampleBlend.ZoomOnly && Settings.SampleZoom == 0) return 0;
        double age = Math.Max(0, Stopwatch.GetElapsedTime(BeatTimestamp, now).TotalSeconds);
        double fade = Math.Clamp(1 - age / (Settings.SampleFade / 1000.0), 0, 1);
        return Settings.SampleOpacity / 100.0 * fade * (0.65 + 0.35 * Pulse(now));
    }
}

// Count each onset once on the render thread; capture workers consume immutable requests.
internal sealed class ScreenSamplePlayback
{
    private VisualizerSettings _settings = new();
    private long _lastBeat;
    private long _generation;
    private int _beats;
    public ScreenSampleRequest Request { get; private set; } = new(new(), 0, 0);

    public void ApplySettings(VisualizerSettings settings)
    {
        if (settings.ScreenSamplesEnabled != _settings.ScreenSamplesEnabled
            || settings.SampleReaction != _settings.SampleReaction || settings.SampleBeats != _settings.SampleBeats
            || (settings.SampleReaction == AudioReaction.Focus && settings.GetRange() != _settings.GetRange()))
        {
            _lastBeat = 0;
            _beats = 0;
            Request = new(settings, 0, ++_generation);
        }
        else Request = Request with { Settings = settings };
        _settings = settings;
    }

    public void Update(AudioFrame audio, long now)
    {
        if (!_settings.ScreenSamplesEnabled || audio.Timestamp == 0
            || Stopwatch.GetElapsedTime(audio.Timestamp, now).TotalSeconds is < 0 or > 0.14) return;
        var signal = _settings.SampleReaction switch
        {
            AudioReaction.Full => audio.Full,
            AudioReaction.Low => audio.Low,
            AudioReaction.Mid => audio.Mid,
            AudioReaction.High => audio.High,
            _ => audio.Focused
        };
        if ((_settings.SampleReaction == AudioReaction.Focus && signal.Range != _settings.GetRange())
            || signal.BeatTimestamp <= _lastBeat || signal.BeatTimestamp > now
            || Stopwatch.GetElapsedTime(signal.BeatTimestamp, now).TotalSeconds > 0.25) return;
        _lastBeat = signal.BeatTimestamp;
        if (_beats == 0) _generation++;
        _beats = (_beats + 1) % _settings.SampleBeats;
        Request = new(_settings, _lastBeat, _generation);
    }
}
