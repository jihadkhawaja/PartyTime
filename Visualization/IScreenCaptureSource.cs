using System;

namespace PartyTime.Visualization;

internal interface IScreenCaptureSource : IDisposable
{
    // A worker can retain its last successful frame in the same untouched buffer.
    bool ReadPixels(byte[] destination, bool retainPreviousPixels = false);
    string? Error { get; }
    long Version { get; }
    int MaximumFrameRate { get; }
}
