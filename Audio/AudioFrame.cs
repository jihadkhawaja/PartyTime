namespace PartyTime.Audio;

internal sealed record AudioFrame(float Level, float Drive, float[] Bands, long Timestamp,
    long BeatTimestamp, float BeatStrength, FrequencyRange Range)
{
    public static AudioFrame Silent { get; } = new(0, 0, new float[AudioAnalyzer.BandCount], 0, 0, 0, FrequencyRange.Full);
}
