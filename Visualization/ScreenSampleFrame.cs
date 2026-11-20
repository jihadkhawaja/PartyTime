using System;
using System.Threading;
using Avalonia.Media.Imaging;

namespace PartyTime.Visualization;

// The worker, UI and retained draw commands must never share a mutable frame.
// Leases keep a published frame alive while the overlay and preview draw it.
internal sealed class SharedScreenBitmap(Bitmap bitmap) : IDisposable
{
    private int _references = 1;
    public Bitmap Bitmap { get; } = bitmap;
    public SharedScreenBitmap Retain() { Interlocked.Increment(ref _references); return this; }
    public void Dispose() { if (Interlocked.Decrement(ref _references) == 0) Bitmap.Dispose(); }
}

internal sealed class ScreenSampleFrame(SharedScreenBitmap source) : IDisposable
{
    private readonly SharedScreenBitmap _source = source.Retain();
    private int _disposed;
    public Bitmap Bitmap => _source.Bitmap;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _source.Dispose();
    }
}
