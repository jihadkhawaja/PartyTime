using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Media;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct2D1.D2D1;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DirectComposition.DComp;

namespace PartyTime.Visualization;

internal sealed class GpuOverlayRenderer : IDisposable
{
    private readonly ID3D11Device _device = null!;
    private readonly IDCompositionDevice _composition = null!;
    private readonly ID2D1Factory1 _factory = null!;
    private readonly ID2D1DeviceContext _context = null!;
    private readonly GpuSceneDrawing _drawing = null!;
    private readonly List<Surface> _surfaces = [];

    public GpuOverlayRenderer(OverlayWindow[] windows, Rectangle[] bounds)
    {
        try
        {
            D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null,
                out ID3D11Device? device, out Vortice.Direct3D.FeatureLevel _).CheckError();
            _device = device!;
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            _factory = D2D1CreateFactory<ID2D1Factory1>();
            using var d2d = _factory.CreateDevice(dxgiDevice);
            _composition = DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            _context = d2d.CreateDeviceContext(DeviceContextOptions.None);
            _drawing = new GpuSceneDrawing(_factory, _context);

            for (int i = 0; i < windows.Length; i++)
                _surfaces.Add(new Surface(this, factory, windows[i], bounds[i]));
            _composition.Commit().CheckError();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Draw(int displayIndex, Scene? scene) => _surfaces[displayIndex].Draw(scene);

    public void Dispose()
    {
        if (_context != null)
            _context.Target = null;
        foreach (var surface in _surfaces)
            surface.Dispose();
        _surfaces.Clear();
        _composition?.Commit();
        _drawing?.Dispose();
        _context?.Dispose();
        _factory?.Dispose();
        _composition?.Dispose();
        _device?.Dispose();
    }

    private sealed class Surface : IDisposable
    {
        private readonly GpuOverlayRenderer _renderer;
        private readonly OverlayWindow _window;
        private readonly Rectangle _bounds;
        private readonly IDXGISwapChain1 _swapChain = null!;
        private readonly IDCompositionTarget _target = null!;
        private readonly IDCompositionVisual _visual = null!;
        private readonly ID2D1Bitmap1 _bitmap = null!;
        private bool _visible;

        public Surface(GpuOverlayRenderer renderer, IDXGIFactory2 factory, OverlayWindow window, Rectangle bounds)
        {
            _renderer = renderer;
            _window = window;
            _bounds = bounds;
            try
            {
                _swapChain = factory.CreateSwapChainForComposition(renderer._device, new SwapChainDescription1
                {
                    Width = (uint)bounds.Width,
                    Height = (uint)bounds.Height,
                    Format = Format.B8G8R8A8_UNorm,
                    BufferCount = 2,
                    SampleDescription = new SampleDescription(1, 0),
                    BufferUsage = Usage.RenderTargetOutput,
                    SwapEffect = SwapEffect.FlipSequential,
                    Scaling = Scaling.Stretch,
                    AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied
                });
                using var backBuffer = _swapChain.GetBuffer<IDXGISurface>(0);
                _bitmap = renderer._context.CreateBitmapFromDxgiSurface(backBuffer, new BitmapProperties1(
                    new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                    96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));

                renderer._composition.CreateTargetForHwnd(window.Handle, true, out var target).CheckError();
                _target = target;
                renderer._composition.CreateVisual(out var visual).CheckError();
                _visual = visual;
                _visual.SetContent(_swapChain).CheckError();
                _target.SetRoot(_visual).CheckError();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Draw(Scene? scene)
        {
            if (scene is null && !_visible)
                return;

            var context = _renderer._context;
            var dpi = VisualTreeHelper.GetDpi(_window);
            context.Target = _bitmap;
            context.SetDpi((float)dpi.PixelsPerInchX, (float)dpi.PixelsPerInchY);
            context.BeginDraw();
            try
            {
                context.Clear(new Color4(0, 0, 0, 0));
                scene?.Draw(_renderer._drawing, _bounds.Width / dpi.DpiScaleX, _bounds.Height / dpi.DpiScaleY);
            }
            finally
            {
                var result = context.EndDraw();
                context.Target = null;
                result.CheckError();
            }

            // A busy compositor can take the next frame instead of blocking the controls.
            var present = _swapChain.Present(0, PresentFlags.DoNotWait);
            if (present == Vortice.DXGI.ResultCode.WasStillDrawing)
                return;
            present.CheckError();
            _visible = scene != null;
        }

        public void Dispose()
        {
            _target?.SetRoot(null);
            _bitmap?.Dispose();
            _visual?.Dispose();
            _target?.Dispose();
            _swapChain?.Dispose();
        }
    }
}
