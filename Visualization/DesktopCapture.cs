using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Platform;
using PartyTime.Interop;

namespace PartyTime.Visualization;

// Captures are raw BGRA in memory. Each worker owns its native resources.
internal sealed class DesktopCapture : IScreenCaptureSource
{
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || OperatingSystem.IsMacOSVersionAtLeast(13);
    public static string UnavailableReason => OperatingSystem.IsLinux()
        ? "Screen effects need capture that excludes PartyTime. This desktop does not provide a supported capture-exclusion API. Palette effects, images, audio, and motion remain available."
        : "Screen effects require Windows 10 2004+ or macOS 13+, with screen recording permission.";
    private readonly PixelRect _bounds;
    private readonly int _width, _height;
    private readonly ulong _displayId;
    private IntPtr _screenDc, _memoryDc, _bitmap, _previousBitmap, _pixels;
    private Process? _process;
    private CancellationTokenSource? _cancellation;
    private byte[]? _latest;
    private readonly object _macGate = new();
    private string? _error;
    private long _retryAfter;
    private long _gpuRetryAfter;
    private GpuScreenCapture? _gpu;
    private bool _tryGpu = true;
    private long _version;
    private long _receivedVersion;
    private byte[]? _reusableDestination;
    private readonly int _requestedFrameRate;
    public long Version => Volatile.Read(ref _version);
    public int MaximumFrameRate => Math.Min(_requestedFrameRate, OperatingSystem.IsWindows() && _gpu == null ? 30 : 60);
    public string Backend => OperatingSystem.IsWindows() ? _gpu != null ? "DXGI GPU downscale" : "GDI fallback" : "ScreenCaptureKit";
    public string? Error => Volatile.Read(ref _error);

    public DesktopCapture(Screen screen, int width, int height, bool preferGpu = true, int frameRate = 60)
    {
        _bounds = screen.Bounds;
        _width = width; _height = height;
        _tryGpu = preferGpu; _requestedFrameRate = Math.Clamp(frameRate, 1, 60);
        _displayId = (ulong)(screen.TryGetPlatformHandle()?.Handle.ToInt64() ?? 0);
    }

    public bool ReadPixels(byte[] destination, bool retainPreviousPixels = false)
    {
        bool previousPixelsValid = retainPreviousPixels && ReferenceEquals(destination, _reusableDestination);
        _reusableDestination = null;
        if (OperatingSystem.IsWindows())
        {
            if (ReadGpu(destination, previousPixelsValid))
            {
                if (retainPreviousPixels) _reusableDestination = destination;
                return true;
            }
            EnsureWindowsCapture();
            if (!StretchBlt(_memoryDc, 0, 0, _width, _height, _screenDc, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0x00CC0020))
                throw new InvalidOperationException("Desktop capture is unavailable.");
            GdiFlush();
            Marshal.Copy(_pixels, destination, 0, destination.Length);
            for (int i = 3; i < destination.Length; i += 4) destination[i] = 255;
            Interlocked.Increment(ref _version);
            return true;
        }
        if (!OperatingSystem.IsMacOS()) return false;
        if (_process == null || _process.HasExited)
        {
            if (Stopwatch.GetTimestamp() < _retryAfter) return false;
            _process?.Dispose();
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            var start = MacCapture.CreateStartInfo("screen", _displayId.ToString(CultureInfo.InvariantCulture),
                _bounds.X.ToString(CultureInfo.InvariantCulture), _bounds.Y.ToString(CultureInfo.InvariantCulture),
                _width.ToString(CultureInfo.InvariantCulture), _height.ToString(CultureInfo.InvariantCulture), _requestedFrameRate.ToString(CultureInfo.InvariantCulture));
            var process = Process.Start(start) ?? throw new IOException("Screen capture could not start.");
            _process = process;
            _retryAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
            _ = ReadMacFrames(process, _cancellation.Token);
        }
        lock (_macGate)
        {
            if (_latest == null) return false;
            if (!previousPixelsValid || _receivedVersion != _version)
                _latest.CopyTo(destination, 0);
            Volatile.Write(ref _version, _receivedVersion);
            if (retainPreviousPixels) _reusableDestination = destination;
        }
        return true;
    }

    private async Task ReadMacFrames(Process process, CancellationToken token)
    {
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            var pixels = new byte[_width * _height * 4];
            while (!token.IsCancellationRequested)
            {
                await process.StandardOutput.BaseStream.ReadExactlyAsync(pixels, token);
                lock (_macGate)
                {
                    var previous = _latest;
                    _latest = pixels;
                    _receivedVersion++;
                    pixels = previous ?? new byte[pixels.Length];
                }
                Volatile.Write(ref _error, null);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            if (!token.IsCancellationRequested && ReferenceEquals(Volatile.Read(ref _process), process))
            {
                try { Volatile.Write(ref _error, (await stderr).Trim()); }
                catch (OperationCanceledException) { }
                lock (_macGate) _latest = null;
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool ReadGpu(byte[] destination, bool previousPixelsValid)
    {
        try
        {
            if (_gpu == null && _tryGpu && Stopwatch.GetTimestamp() >= _gpuRetryAfter)
            {
                _gpu = GpuScreenCapture.Open(_bounds, _width, _height);
                _tryGpu = _gpu != null;
            }
            if (_gpu == null || !_gpu.TryReadPixels(destination, previousPixelsValid)) return false;
            if (_gpu.Changed) Interlocked.Increment(ref _version);
            return true;
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            Debug.WriteLine(ex);
            _gpu?.Dispose(); _gpu = null;
            _gpuRetryAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
            return false;
        }
    }

    private void EnsureWindowsCapture()
    {
        if (_bitmap != IntPtr.Zero) return;
        _screenDc = GetDC(IntPtr.Zero);
        _memoryDc = CreateCompatibleDC(_screenDc);
        var header = new BitmapInfoHeader { Size = 40, Width = _width, Height = -_height, Planes = 1, BitCount = 32 };
        _bitmap = CreateDIBSection(_screenDc, ref header, 0, out _pixels, IntPtr.Zero, 0);
        if (_screenDc == IntPtr.Zero || _memoryDc == IntPtr.Zero || _bitmap == IntPtr.Zero)
        {
            Dispose();
            throw new InvalidOperationException("Desktop capture could not allocate a bitmap.");
        }
        _previousBitmap = SelectObject(_memoryDc, _bitmap);
        SetStretchBltMode(_memoryDc, 4);
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows()) { _gpu?.Dispose(); _gpu = null; }
        _cancellation?.Cancel();
        if (_process != null)
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _process.Dispose(); _process = null;
        }
        _cancellation?.Dispose(); _cancellation = null;
        if (_previousBitmap != IntPtr.Zero) SelectObject(_memoryDc, _previousBitmap);
        if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
        if (_memoryDc != IntPtr.Zero) DeleteDC(_memoryDc);
        if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
        _bitmap = _memoryDc = _screenDc = _previousBitmap = _pixels = IntPtr.Zero;
        _reusableDestination = null;
        lock (_macGate) _latest = null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
}
