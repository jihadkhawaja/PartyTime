using System;
using System.Numerics;
using System.Runtime.InteropServices;
using PartyTime.Settings;

namespace PartyTime.Visualization;

// Match the WPF screen effects in capture pixels before the renderer scales and fades the image.
internal static class ScreenBlendKernels
{
    private static readonly Vector<byte> Alpha = Vector.AsVectorByte(new Vector<uint>(0xff000000u));
    private interface IKernel
    {
        static abstract uint Pixel(uint background, uint sample);
        static abstract Vector<byte> Pixels(Vector<byte> background, Vector<byte> sample);
    }

    public static void Blend(ReadOnlySpan<byte> background, ReadOnlySpan<byte> sample, Span<byte> output,
        int width, int height, ScreenSampleBlend blend, double zoom)
    {
        if (width <= 0 || height <= 0 || !double.IsFinite(zoom) || zoom < 1) throw new ArgumentOutOfRangeException(nameof(zoom));
        int length = checked(width * height * 4);
        if (background.Length < length || sample.Length < length || output.Length < length) throw new ArgumentException("Pixel buffers are too small.");
        background = background[..length]; sample = sample[..length]; output = output[..length];
        switch (blend)
        {
            case ScreenSampleBlend.Additive: Run<Add>(background, sample, output, width, height, zoom); break;
            case ScreenSampleBlend.Screen: Run<Screen>(background, sample, output, width, height, zoom); break;
            case ScreenSampleBlend.Multiply: Run<Multiply>(background, sample, output, width, height, zoom); break;
            case ScreenSampleBlend.Difference: Run<Difference>(background, sample, output, width, height, zoom); break;
            case ScreenSampleBlend.Negative: Run<Negative>(background, sample, output, width, height, zoom); break;
            default: Run<Normal>(background, sample, output, width, height, zoom); break;
        }
    }

    private static void Run<T>(ReadOnlySpan<byte> background, ReadOnlySpan<byte> sample, Span<byte> output,
        int width, int height, double zoom) where T : struct, IKernel
    {
        var bg = MemoryMarshal.Cast<byte, uint>(background);
        var held = MemoryMarshal.Cast<byte, uint>(sample);
        var target = MemoryMarshal.Cast<byte, uint>(output);
        if (zoom == 1)
        {
            int i = 0;
            if (Vector.IsHardwareAccelerated)
                for (; i <= output.Length - Vector<byte>.Count; i += Vector<byte>.Count)
                    (T.Pixels(new Vector<byte>(background.Slice(i)), new Vector<byte>(sample.Slice(i))) | Alpha).CopyTo(output.Slice(i));
            for (int pixel = i / 4; pixel < target.Length; pixel++) target[pixel] = T.Pixel(bg[pixel], held[pixel]) | 0xff000000u;
            return;
        }
        int[]? rented = width > 2048 ? System.Buffers.ArrayPool<int>.Shared.Rent(width) : null;
        Span<int> columns = rented != null ? rented.AsSpan(0, width) : stackalloc int[width];
        try
        {
            for (int x = 0; x < width; x++) columns[x] = Math.Clamp((int)((x - width / 2.0) / zoom + width / 2.0), 0, width - 1);
            for (int y = 0; y < height; y++)
            {
                int sy = Math.Clamp((int)((y - height / 2.0) / zoom + height / 2.0), 0, height - 1) * width;
                int row = y * width;
                for (int x = 0; x < width; x++) target[row + x] = T.Pixel(bg[row + x], held[sy + columns[x]]) | 0xff000000u;
            }
        }
        finally { if (rented != null) System.Buffers.ArrayPool<int>.Shared.Return(rented); }
    }

    private readonly struct Normal : IKernel
    {
        public static uint Pixel(uint b, uint s) => s;
        public static Vector<byte> Pixels(Vector<byte> b, Vector<byte> s) => s;
    }
    private readonly struct Negative : IKernel
    {
        public static uint Pixel(uint b, uint s) => s ^ 0x00ffffffu;
        public static Vector<byte> Pixels(Vector<byte> b, Vector<byte> s) => ~s;
    }
    private readonly struct Add : IKernel
    {
        public static uint Pixel(uint b, uint s) => Math.Min(255u, (b & 255) + (s & 255))
            | (Math.Min(255u, ((b >> 8) & 255) + ((s >> 8) & 255)) << 8)
            | (Math.Min(255u, ((b >> 16) & 255) + ((s >> 16) & 255)) << 16);
        public static Vector<byte> Pixels(Vector<byte> b, Vector<byte> s)
        {
            Vector.Widen(b, out Vector<ushort> bl, out Vector<ushort> bh); Vector.Widen(s, out Vector<ushort> sl, out Vector<ushort> sh);
            var max = new Vector<ushort>(255);
            return Vector.Narrow(Vector.Min(bl + sl, max), Vector.Min(bh + sh, max));
        }
    }
    private readonly struct Multiply : IKernel
    {
        public static uint Pixel(uint b, uint s) => ((b & 255) * (s & 255) / 255)
            | ((((b >> 8) & 255) * ((s >> 8) & 255) / 255) << 8)
            | ((((b >> 16) & 255) * ((s >> 16) & 255) / 255) << 16);
        public static Vector<byte> Pixels(Vector<byte> b, Vector<byte> s)
        {
            Vector.Widen(b, out Vector<ushort> bl, out Vector<ushort> bh); Vector.Widen(s, out Vector<ushort> sl, out Vector<ushort> sh);
            return Vector.Narrow(Divide255(bl * sl), Divide255(bh * sh));
        }
        private static Vector<ushort> Divide255(Vector<ushort> value) => (value + Vector<ushort>.One + (value >> 8)) >> 8;
    }
    private readonly struct Screen : IKernel
    {
        public static uint Pixel(uint b, uint s) => Multiply.Pixel(b ^ 0x00ffffffu, s ^ 0x00ffffffu) ^ 0x00ffffffu;
        public static Vector<byte> Pixels(Vector<byte> b, Vector<byte> s) => ~Multiply.Pixels(~b, ~s);
    }
    private readonly struct Difference : IKernel
    {
        public static uint Pixel(uint b, uint s) => (uint)Math.Abs((int)(b & 255) - (int)(s & 255))
            | ((uint)Math.Abs((int)((b >> 8) & 255) - (int)((s >> 8) & 255)) << 8)
            | ((uint)Math.Abs((int)((b >> 16) & 255) - (int)((s >> 16) & 255)) << 16);
        public static Vector<byte> Pixels(Vector<byte> b, Vector<byte> s) => Vector.Max(b, s) - Vector.Min(b, s);
    }
}
