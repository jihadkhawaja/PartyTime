using System;
using Rectangle = Avalonia.PixelRect;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct2D1.D2D1;
using static Vortice.Direct3D11.D3D11;

namespace PartyTime.Visualization;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class GpuScreenCapture : IDisposable
{
    private readonly ID3D11Device _device = null!;
    private readonly ID3D11DeviceContext _immediate = null!;
    private readonly IDXGIOutputDuplication _duplication = null!;
    private readonly ID2D1DeviceContext _context = null!;
    private readonly ID3D11Texture2D _source = null!;
    private readonly ID2D1Bitmap1 _sourceBitmap = null!;
    private readonly ID2D1Bitmap1 _target = null!;
    private readonly ID2D1Bitmap1 _readback = null!;
    private readonly int _width, _height;
    private bool _ownsFrame;
    public bool HasFrame { get; private set; }
    public bool Changed { get; private set; }

    private GpuScreenCapture(IDXGIAdapter adapter, IDXGIOutput1 output, Rectangle bounds, int width, int height)
    {
        _width = width;
        _height = height;
        try
        {
            D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null!,
                out ID3D11Device device, out ID3D11DeviceContext immediate).CheckError();
            _device = device;
            _immediate = immediate;
            _duplication = output.DuplicateOutput(device);

            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var factory = D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
            using var d2d = factory.CreateDevice(dxgiDevice);
            _context = d2d.CreateDeviceContext(DeviceContextOptions.None);

            var format = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore);
            _source = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
                (uint)bounds.Width, (uint)bounds.Height, 1, 1, BindFlags.ShaderResource));
            using var sourceSurface = _source.QueryInterface<IDXGISurface>();
            _sourceBitmap = _context.CreateBitmapFromDxgiSurface(sourceSurface, new BitmapProperties1(format, 96, 96));

            var size = new Vortice.Mathematics.SizeI(width, height);
            _target = _context.CreateBitmap(size, new BitmapProperties1(format, 96, 96, BitmapOptions.Target));
            _readback = _context.CreateBitmap(size, new BitmapProperties1(format, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
            _context.Target = _target;
            _context.SetDpi(96, 96);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static GpuScreenCapture? Open(Rectangle bounds, int width = ScreenColorSampler.SampleWidth, int height = ScreenColorSampler.SampleHeight)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            using (adapter)
            {
                if ((adapter.Description1.Flags & AdapterFlags.Software) != 0)
                    continue;

                for (uint j = 0; adapter.EnumOutputs(j, out var output).Success; j++)
                {
                    using (output)
                    {
                        var description = output.Description;
                        var rect = description.DesktopCoordinates;
                        if (!description.AttachedToDesktop || rect.Left != bounds.X || rect.Top != bounds.Y
                            || rect.Right != bounds.Right || rect.Bottom != bounds.Bottom)
                            continue;

                        // The desktop duplication surface is unrotated. The GDI path handles rotated displays.
                        if (description.Rotation != ModeRotation.Identity)
                            return null;
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        return new GpuScreenCapture(adapter, output1, bounds, width, height);
                    }
                }
            }
        }
        return null;
    }

    public unsafe bool TryReadPixels(Span<byte> pixels, bool previousPixelsValid)
    {
        Changed = UpdateFrame();
        if (!HasFrame) return false;
        if (!Changed && previousPixelsValid) return true;
        var mapped = _readback.Map(MapOptions.Read);
        try
        {
            for (int y = 0; y < _height; y++)
                new ReadOnlySpan<byte>((byte*)mapped.Bits + y * mapped.Pitch, _width * 4)
                    .CopyTo(pixels.Slice(y * _width * 4, _width * 4));
        }
        finally { _readback.Unmap(); }
        return true;
    }

    private bool UpdateFrame()
    {
        ReleaseFrame();
        var result = _duplication.AcquireNextFrame(0, out var frame, out var resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            return false;
        result.CheckError();
        _ownsFrame = true;
        using (resource)
        {
            if (frame.LastPresentTime == 0 && HasFrame)
                return false;

            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            _immediate.CopyResource(_source, texture);
            _context.BeginDraw();
            _context.DrawBitmap(_sourceBitmap,
                new RawRectF(0, 0, _width, _height),
                1, _width == ScreenColorSampler.SampleWidth ? Vortice.Direct2D1.InterpolationMode.NearestNeighbor
                    : Vortice.Direct2D1.InterpolationMode.Linear, null, null);
            _context.EndDraw().CheckError();
        }

        _readback.CopyFromBitmap(_target);
        HasFrame = true;
        return true;
    }

    private void ReleaseFrame()
    {
        if (!_ownsFrame)
            return;

        // Keeping the frame acquired between polls avoids extra full-frame copies in DXGI.
        _ownsFrame = false;
        _duplication.ReleaseFrame().CheckError();
    }

    public void Dispose()
    {
        if (_ownsFrame)
            _duplication.ReleaseFrame();
        if (_context != null)
            _context.Target = null;
        _readback?.Dispose();
        _target?.Dispose();
        _sourceBitmap?.Dispose();
        _source?.Dispose();
        _context?.Dispose();
        _duplication?.Dispose();
        _immediate?.Dispose();
        _device?.Dispose();
    }
}
