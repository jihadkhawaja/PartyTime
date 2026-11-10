namespace PartyTime.Audio;

internal sealed record FrequencyRange(int Low, int High)
{
    public const int Minimum = 40;
    public const int Maximum = 13_000;
    public const int MinimumWidth = 50;
    public static FrequencyRange Full { get; } = new(Minimum, Maximum);
    public static FrequencyRange Bass { get; } = new(Minimum, 250);
    public static FrequencyRange Vocals { get; } = new(300, 3_400);
    public static FrequencyRange Mids { get; } = new(250, 2_000);
    public static FrequencyRange Treble { get; } = new(2_000, Maximum);

    public static bool IsValid(int low, int high)
        => low >= Minimum && high <= Maximum && high >= (long)low + MinimumWidth;
}
