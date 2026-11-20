using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using PartyTime.Settings;

namespace PartyTime.Visualization;

internal sealed class BeatScreenSampler : IDisposable
{
    private readonly Timer _timer;
    private readonly IScreenCaptureSource _capture;
    private readonly int _width, _height;
    private readonly byte[] _pixels, _heldPixels, _output;
    private readonly byte[]? _palettePixels;
    private readonly object _gate = new();
    private readonly bool _automatic;
    private SharedScreenBitmap? _latest;
    private ScreenSampleBlend _latestBlend;
    private int _latestStrength;
    private long _latestGeneration = -1, _latestCaptureVersion = -1;
    private double _latestZoom;
    private byte[]? _filtered, _blurScratch;
    private (long Generation, ScreenSampleBlend Blend, int Strength) _filterKey;
    private ScreenSampleRequest? _request;
    private Color[]? _colors;
    private string? _error;
    private long _generation = -1, _lastPalette, _retryAfter, _frameDeadline;
    private int _state;
    public bool HasFrame { get { lock (_gate) return _latest != null; } }
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
        _pixels = new byte[_width * _height * 4];
        _heldPixels = new byte[_pixels.Length];
        _output = new byte[_pixels.Length];
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
            || previous.Settings.SampleBlend != request.Settings.SampleBlend || previous.Settings.SampleZoom != request.Settings.SampleZoom
            || FilterStrength(previous.Settings) != FilterStrength(request.Settings)
            || previous.Opacity(now) <= 0.005 && request.Opacity(now) > 0.005;
        if (_automatic && wake)
        {
            lock (_gate)
                if (Volatile.Read(ref _state) == 0) SetTimer(0);
        }
    }

    public ScreenSampleFrame? AcquireFrame(ScreenSampleBlend blend)
    {
        lock (_gate)
        {
            return _latest != null && _latestBlend == blend ? new ScreenSampleFrame(_latest) : null;
        }
    }

    internal bool CaptureFrame() => CaptureFrame(Stopwatch.GetTimestamp());

    internal bool CaptureFrame(long now)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return false;
        bool captured = false;
        try
        {
            if (now < _retryAfter) return false;
            var request = Volatile.Read(ref _request);
            bool visible = request?.Opacity(now) > 0.005;
            bool liveZoom = request?.Settings.SampleBlend == ScreenSampleBlend.ZoomOnly;
            bool newSample = visible && !liveZoom && request!.Generation != _generation;
            bool live = visible && NeedsBackground(request!.Settings.SampleBlend);
            bool colorsDue = _palettePixels != null && (_lastPalette == 0 || Stopwatch.GetElapsedTime(_lastPalette, now).TotalMilliseconds >= 250);
            if (newSample || live || colorsDue)
            {
                captured = _capture.ReadPixels(_pixels);
                if (captured && colorsDue)
                {
                    DownsamplePalette(_pixels, _width, _height, _palettePixels!);
                    Volatile.Write(ref _colors, ScreenColorSampler.ExtractPalette(_palettePixels!, ScreenColorSampler.SampleWidth * 4));
                    _lastPalette = now;
                }
                if (captured && newSample)
                {
                    _pixels.CopyTo(_heldPixels, 0);
                    _generation = request!.Generation;
                }
            }
            if (request != null && visible && (liveZoom || _generation == request.Generation) && (!live || captured))
                PublishFrame(request, now, live ? _capture.Version : -1);
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
            lock (_gate)
            {
                if (Interlocked.CompareExchange(ref _state, 0, 1) == 2) Release();
                else Schedule();
            }
        }
    }

    private static bool NeedsBackground(ScreenSampleBlend blend)
        => blend is ScreenSampleBlend.Additive or ScreenSampleBlend.Screen or ScreenSampleBlend.Multiply
            or ScreenSampleBlend.Difference or ScreenSampleBlend.ZoomOnly;

    private static int FilterStrength(VisualizerSettings settings)
        => settings.SampleBlend is ScreenSampleBlend.Blur or ScreenSampleBlend.Pixelate ? settings.SampleEffectStrength : 0;

    private byte[] GetFilteredSample(ScreenSampleBlend blend, int strength)
    {
        var key = (_generation, blend, strength);
        if (_filterKey == key && _filtered != null) return _filtered;

        _filtered ??= new byte[_heldPixels.Length];
        if (blend == ScreenSampleBlend.Blur)
        {
            _blurScratch ??= new byte[_heldPixels.Length];
            ScreenSampleFilters.Blur(_heldPixels, _filtered, _blurScratch, _width, _height, strength);
        }
        else
            ScreenSampleFilters.Pixelate(_heldPixels, _filtered, _width, _height, strength);

        _filterKey = key;
        return _filtered;
    }

    private void PublishFrame(ScreenSampleRequest request, long now, long captureVersion)
    {
        var blend = request.Settings.SampleBlend;
        bool liveZoom = blend == ScreenSampleBlend.ZoomOnly;
        int strength = FilterStrength(request.Settings);
        // Live Zoom Only changes the texture crop in the renderer, so a static desktop needs only one upload.
        double zoom = liveZoom ? 1 : request.Zoom(now);
        if ((liveZoom || _latestGeneration == _generation) && _latestBlend == blend && _latestCaptureVersion == captureVersion
            && _latestZoom == zoom && _latestStrength == strength)
            return;

        // Sample the held pixels, blend with the live desktop, then present a single opaque
        // image. Scaling the inputs first changes clipping and cancellation at edges.
        byte[] sample = liveZoom ? _pixels : strength > 0 ? GetFilteredSample(blend, strength) : _heldPixels;
        var pixelBlend = strength > 0 || liveZoom ? ScreenSampleBlend.Normal : blend;
        BlendPixels(_pixels, sample, _output, _width, _height, pixelBlend, zoom);
        var next = new SharedScreenBitmap(BitmapPixels.Create(_output, _width, _height));
        lock (_gate)
        {
            var previous = _latest;
            _latest = next;
            _latestBlend = blend;
            _latestStrength = strength;
            _latestGeneration = _generation;
            _latestCaptureVersion = captureVersion;
            _latestZoom = zoom;
            previous?.Dispose();
        }
    }

    private void Schedule()
    {
        if (!_automatic || Volatile.Read(ref _state) == 2) return;
        var request = Volatile.Read(ref _request);
        long now = Stopwatch.GetTimestamp();
        int delay = _palettePixels != null ? 250 : Timeout.Infinite;
        if (request?.Opacity(now) > 0.005)
        {
            if (request.Generation != _latestGeneration || request.Settings.SampleBlend != _latestBlend
                || NeedsBackground(request.Settings.SampleBlend) || request.Settings.SampleZoom > 0 || _latestZoom != 1
                || FilterStrength(request.Settings) != _latestStrength)
            {
                long period = FramePacing.Period(_capture.MaximumFrameRate);
                if (_frameDeadline <= now)
                    _frameDeadline = FramePacing.Advance(_frameDeadline, now, period);
                delay = Math.Max(1, (int)Math.Ceiling((_frameDeadline - now) * 1000.0 / Stopwatch.Frequency));
            }
        }
        if (_retryAfter > now) delay = Math.Max(1, (int)Stopwatch.GetElapsedTime(now, _retryAfter).TotalMilliseconds);
        SetTimer(delay);
    }

    private void SetTimer(int delay)
    {
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
            _latest?.Dispose();
            _latest = null;
        }
        Volatile.Write(ref _colors, null);
    }
}
