using System;
using System.Diagnostics;

namespace PartyTime.Visualization;

internal static class FramePacing
{
    public static double CursorRate(double? displayRate)
        => Math.Clamp(displayRate is > 0 ? displayRate.Value : 60, 20, 120);

    public static long Period(double rate) => Math.Max(1, (long)Math.Round(Stopwatch.Frequency / rate));

    public static long Advance(long deadline, long now, long period)
        => deadline + ((now - deadline) / period + 1) * period;
}
