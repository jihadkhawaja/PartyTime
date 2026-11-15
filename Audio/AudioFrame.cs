namespace PartyTime.Audio;

internal sealed record AudioSignal(float Level, float Drive, float[] Bands,
    long BeatTimestamp, float BeatStrength, FrequencyRange Range)
{
    public static AudioSignal Silent { get; } = new(0, 0, new float[AudioAnalyzer.BandCount], 0, 0, FrequencyRange.Full);
}

internal sealed record AudioFrame(long Timestamp, AudioSignal Focused, AudioSignal Full,
    AudioSignal Low, AudioSignal Mid, AudioSignal High)
{
    public static AudioFrame Silent { get; } = new(0, AudioSignal.Silent, AudioSignal.Silent,
        AudioSignal.Silent, AudioSignal.Silent, AudioSignal.Silent);
}
