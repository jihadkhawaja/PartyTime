using System;

namespace PartyTime.Visualization;

internal interface IScreenCaptureSource : IDisposable
{
    bool ReadPixels(byte[] destination);
    string? Error { get; }
    long Version { get; }
    int MaximumFrameRate { get; }
}
