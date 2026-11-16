using System;
using System.Threading;
using Avalonia.Media.Imaging;

namespace PartyTime.Visualization;

// The worker, UI and retained draw commands must never share a mutable frame.
// Leases keep only the newest current frame plus the held beat sample alive.
internal sealed class SharedScreenBitmap(Bitmap bitmap) : IDisposable
{
    private int _references = 1;
    public Bitmap Bitmap { get; } = bitmap;
    public SharedScreenBitmap Retain() { Interlocked.Increment(ref _references); return this; }
    public void Dispose() { if (Interlocked.Decrement(ref _references) == 0) Bitmap.Dispose(); }
}

internal sealed class ScreenSampleFrame : IDisposable
{
    private readonly SharedScreenBitmap _held;
    private readonly SharedScreenBitmap? _current;
    private int _disposed;
    public Bitmap Held => _held.Bitmap;
    public Bitmap? Current => _current?.Bitmap;
    public bool Negative { get; }
    public ScreenSampleFrame(SharedScreenBitmap held, SharedScreenBitmap? current, bool negative)
    {
        _held = held.Retain(); _current = current?.Retain(); Negative = negative;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _held.Dispose(); _current?.Dispose();
    }
}
