using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PartyTime.Settings;

namespace PartyTime.Visualization;

internal sealed class BeatScreenSampler : IDisposable
{
    private readonly Timer _timer;
    private readonly IScreenCaptureSource _capture;
    private readonly int _width, _height;
    private readonly byte[] _pixels, _heldPixels;
    private byte[]? _negativePixels;
    private readonly byte[]? _palettePixels;
    private readonly object _gate = new();
    private readonly bool _automatic;
    private SharedScreenBitmap? _current, _held, _negative;
    private ScreenSampleRequest? _request;
    private Color[]? _colors;
    private string? _error;
    private long _generation = -1, _lastPalette, _retryAfter, _lastCaptureVersion = -1;
    private long _captureDeadline;
    private int _state;
    public bool HasFrame { get { lock (_gate) return _held != null; } }
    public Color[]? Colors => Volatile.Read(ref _colors);
    public string? Error => Volatile.Read(ref _error) ?? _capture.Error;

    public BeatScreenSampler(Screen screen, bool automaticCapture = true, bool sampleColors = false)
        : this(CaptureSize(screen).Width, CaptureSize(screen).Height,
            new DesktopCapture(screen, CaptureSize(screen).Width, CaptureSize(screen).Height), automaticCapture, sampleColors) { }

    private static PixelSize CaptureSize(Screen screen)
    {
        double scale = Math.Min(1, 960.0 / Math.Max(screen.Bounds.Width, screen.Bounds.Height));
        return new PixelSize(Math.Max(1, (int)Math.Round(screen.Bounds.Width * scale)), Math.Max(1, (int)Math.Round(screen.Bounds.Height * scale)));
    }

    internal BeatScreenSampler(int width, int height, IScreenCaptureSource capture, bool automaticCapture = false, bool sampleColors = false)
    {
        _width = width; _height = height;
        _pixels = new byte[_width * _height * 4]; _heldPixels = new byte[_pixels.Length];
        _palettePixels = sampleColors ? new byte[ScreenColorSampler.SampleWidth * ScreenColorSampler.SampleHeight * 4] : null;
        _capture = capture;
        _automatic = automaticCapture;
        _timer = new Timer(_ => CaptureFrame(), null, Timeout.Infinite, Timeout.Infinite);
        if (sampleColors && automaticCapture) _timer.Change(0, Timeout.Infinite);
    }

    public void Update(ScreenSampleRequest request)
    {
        var previous = Interlocked.Exchange(ref _request, request);
        if (ReferenceEquals(previous, request)) return;
        // A new beat wakes a sleeping worker immediately; fading a held sample needs no capture.
        long now = Stopwatch.GetTimestamp();
        bool wake = previous == null || previous.Generation != request.Generation || previous.BeatTimestamp != request.BeatTimestamp
            || ScreenSampleLayer.NeedsBackground(previous.Settings.SampleBlend) != ScreenSampleLayer.NeedsBackground(request.Settings.SampleBlend)
            || previous.Settings.SampleBlend != request.Settings.SampleBlend && request.Settings.SampleBlend == ScreenSampleBlend.Negative
            || previous.Opacity(now) <= 0.005 && request.Opacity(now) > 0.005;
        if (_automatic && wake && Volatile.Read(ref _state) == 0)
            try { _timer.Change(0, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    public ScreenSampleFrame? AcquireFrame(ScreenSampleBlend blend)
    {
        lock (_gate)
        {
            var held = blend == ScreenSampleBlend.Negative ? _negative : _held;
            return held == null ? null : new ScreenSampleFrame(held, ScreenSampleLayer.NeedsBackground(blend) ? _current : null, blend == ScreenSampleBlend.Negative);
        }
    }

    internal bool CaptureFrame()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return false;
        bool captured = false;
        try
        {
            long now = Stopwatch.GetTimestamp();
            if (now < _retryAfter) return false;
            var request = Volatile.Read(ref _request);
            bool visible = request?.Opacity(now) > 0.005;
            bool newSample = visible && request!.Generation != _generation;
            bool live = visible && ScreenSampleLayer.NeedsBackground(request!.Settings.SampleBlend);
            bool colorsDue = _palettePixels != null && (_lastPalette == 0 || Stopwatch.GetElapsedTime(_lastPalette, now).TotalMilliseconds >= 250);
            if (newSample || live || colorsDue)
            {
                if (!_capture.ReadPixels(_pixels)) return false;
                captured = true;
                long period = FramePacing.Period(_capture.MaximumFrameRate);
                _captureDeadline = _captureDeadline == 0 || now - _captureDeadline > Stopwatch.Frequency
                    ? now + period : FramePacing.Advance(_captureDeadline, Math.Max(now, _captureDeadline), period);
                if (colorsDue)
                {
                    DownsamplePalette(_pixels, _width, _height, _palettePixels!);
                    Volatile.Write(ref _colors, ScreenColorSampler.ExtractPalette(_palettePixels!, ScreenColorSampler.SampleWidth * 4));
                    _lastPalette = now;
                }
                if (newSample || live && _capture.Version != _lastCaptureVersion)
                {
                    var next = new SharedScreenBitmap(BitmapPixels.Create(_pixels, _width, _height));
                    lock (_gate)
                    {
                        var previous = _current; _current = next; previous?.Dispose();
                        if (newSample)
                        {
                            previous = _held; _held = next.Retain(); previous?.Dispose();
                            _negative?.Dispose(); _negative = null;
                            _pixels.CopyTo(_heldPixels, 0); _generation = request!.Generation;
                        }
                    }
                    _lastCaptureVersion = _capture.Version;
                }
            }
            if (visible && request!.Settings.SampleBlend == ScreenSampleBlend.Negative && _negative == null && _generation == request.Generation)
            {
                _negativePixels ??= new byte[_pixels.Length];
                BlendPixels(_heldPixels, _heldPixels, _negativePixels, _width, _height, ScreenSampleBlend.Negative, 1);
                var negative = new SharedScreenBitmap(BitmapPixels.Create(_negativePixels, _width, _height));
                lock (_gate) { _negative?.Dispose(); _negative = negative; }
            }
            Volatile.Write(ref _error, null);
            return captured;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or System.IO.IOException or ArgumentException)
        {
            Volatile.Write(ref _error, ex.Message);
            _retryAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            return false;
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _state, 0, 1) == 2) Release();
            else Schedule();
        }
    }

    private void Schedule()
    {
        if (!_automatic || Volatile.Read(ref _state) == 2) return;
        var request = Volatile.Read(ref _request);
        long now = Stopwatch.GetTimestamp();
        int delay = _palettePixels != null ? 250 : Timeout.Infinite;
        if (_captureDeadline > 0 && _captureDeadline < now)
            _captureDeadline = FramePacing.Advance(_captureDeadline, now, FramePacing.Period(_capture.MaximumFrameRate));
        if (request?.Opacity(now) > 0.005)
            delay = request.Generation != _generation ? 5 : ScreenSampleLayer.NeedsBackground(request.Settings.SampleBlend)
                ? Math.Max(1, (int)Math.Ceiling((_captureDeadline - now) * 1000.0 / Stopwatch.Frequency)) : delay;
        if (_retryAfter > now) delay = Math.Max(1, (int)Stopwatch.GetElapsedTime(now, _retryAfter).TotalMilliseconds);
        try { _timer.Change(delay, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    internal static void DownsamplePalette(ReadOnlySpan<byte> source, int width, int height, Span<byte> output)
    {
        for (int y = 0; y < ScreenColorSampler.SampleHeight; y++)
            for (int x = 0; x < ScreenColorSampler.SampleWidth; x++)
            {
                int from = ((y * height / ScreenColorSampler.SampleHeight) * width + x * width / ScreenColorSampler.SampleWidth) * 4;
                source.Slice(from, 4).CopyTo(output.Slice((y * ScreenColorSampler.SampleWidth + x) * 4, 4));
            }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte BlendChannel(byte background, byte sample, ScreenSampleBlend blend) => blend switch
    {
        ScreenSampleBlend.Additive => (byte)Math.Min(255, background + sample),
        ScreenSampleBlend.Screen => (byte)(255 - (255 - background) * (255 - sample) / 255),
        ScreenSampleBlend.Multiply => (byte)(background * sample / 255),
        ScreenSampleBlend.Difference => (byte)Math.Abs(background - sample),
        ScreenSampleBlend.Negative => (byte)(255 - sample),
        _ => sample
    };

    internal static void BlendPixels(ReadOnlySpan<byte> background, ReadOnlySpan<byte> sample, Span<byte> output,
        int width, int height, ScreenSampleBlend blend, double zoom)
    {
        ScreenBlendKernels.Blend(background, sample, output, width, height, blend, zoom);
    }


    public void Dispose()
    {
        _timer.Dispose();
        if (Interlocked.Exchange(ref _state, 2) == 0) Release();
    }
    private void Release()
    {
        _capture.Dispose();
        lock (_gate)
        {
            _current?.Dispose(); _held?.Dispose(); _negative?.Dispose();
            _current = _held = _negative = null;
        }
        Volatile.Write(ref _colors, null);
    }
}
