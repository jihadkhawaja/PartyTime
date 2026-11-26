using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
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
        if (zoom == 1)
        {
            BlendSpan<T>(background, sample, output);
            return;
        }

        bool readsBackground = typeof(T) != typeof(Normal) && typeof(T) != typeof(Negative);
        bool vectorize = readsBackground && Vector.IsHardwareAccelerated && width >= Vector<uint>.Count;
        bool gather = vectorize && Avx2.IsSupported && Vector<uint>.Count == Vector256<uint>.Count;
        int scratchLength = vectorize && !gather ? checked(width * 2) : width;
        int[]? rented = scratchLength > 2048 ? System.Buffers.ArrayPool<int>.Shared.Rent(scratchLength) : null;
        Span<int> scratch = rented != null ? rented.AsSpan(0, scratchLength) : stackalloc int[scratchLength];
        Span<int> columns = scratch[..width];
        Span<uint> zoomedRow = MemoryMarshal.Cast<int, uint>(scratch[width..]);
        var held = MemoryMarshal.Cast<byte, uint>(sample);
        int rowBytes = width * 4;
        try
        {
            for (int x = 0; x < width; x++) columns[x] = Math.Clamp((int)((x - width / 2.0) / zoom + width / 2.0), 0, width - 1);
            int previousSource = -1;
            for (int y = 0; y < height; y++)
            {
                int source = Math.Clamp((int)((y - height / 2.0) / zoom + height / 2.0), 0, height - 1);
                var sourceRow = held.Slice(source * width, width);
                var backgroundRow = background.Slice(y * rowBytes, rowBytes);
                var outputRow = output.Slice(y * rowBytes, rowBytes);
                if (gather)
                    BlendGatheredRow<T>(backgroundRow, sourceRow, columns, outputRow);
                else if (vectorize)
                {
                    // Resample exactly as in the scalar path, then blend a contiguous row with SIMD.
                    if (source != previousSource)
                        for (int x = 0; x < width; x++) zoomedRow[x] = sourceRow[columns[x]];
                    BlendSpan<T>(backgroundRow, MemoryMarshal.AsBytes(zoomedRow), outputRow);
                    previousSource = source;
                }
                else
                {
                    var bg = MemoryMarshal.Cast<byte, uint>(backgroundRow);
                    var target = MemoryMarshal.Cast<byte, uint>(outputRow);
                    for (int x = 0; x < width; x++)
                        target[x] = T.Pixel(readsBackground ? bg[x] : 0, sourceRow[columns[x]]) | 0xff000000u;
                }
            }
        }
        finally { if (rented != null) System.Buffers.ArrayPool<int>.Shared.Return(rented); }
    }

    private static unsafe void BlendGatheredRow<T>(ReadOnlySpan<byte> background, ReadOnlySpan<uint> source, ReadOnlySpan<int> columns, Span<byte> output)
        where T : struct, IKernel
    {
        int x = 0;
        ref int firstColumn = ref MemoryMarshal.GetReference(columns);
        fixed (uint* pixels = source)
        {
            for (; x <= columns.Length - Vector256<int>.Count; x += Vector256<int>.Count)
            {
                var indices = Vector256.LoadUnsafe(ref firstColumn, (nuint)x);
                var sample = Avx2.GatherVector256((int*)pixels, indices, 4).AsByte().AsVector();
                int offset = x * 4;
                (T.Pixels(new Vector<byte>(background.Slice(offset)), sample) | Alpha).CopyTo(output.Slice(offset));
            }
        }

        var bg = MemoryMarshal.Cast<byte, uint>(background);
        var target = MemoryMarshal.Cast<byte, uint>(output);
        for (; x < columns.Length; x++)
            target[x] = T.Pixel(bg[x], source[columns[x]]) | 0xff000000u;
    }

    private static void BlendSpan<T>(ReadOnlySpan<byte> background, ReadOnlySpan<byte> sample, Span<byte> output)
        where T : struct, IKernel
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
            for (; i <= output.Length - Vector<byte>.Count; i += Vector<byte>.Count)
                (T.Pixels(new Vector<byte>(background.Slice(i)), new Vector<byte>(sample.Slice(i))) | Alpha).CopyTo(output.Slice(i));
        var bg = MemoryMarshal.Cast<byte, uint>(background);
        var held = MemoryMarshal.Cast<byte, uint>(sample);
        var target = MemoryMarshal.Cast<byte, uint>(output);
        for (int pixel = i / 4; pixel < target.Length; pixel++) target[pixel] = T.Pixel(bg[pixel], held[pixel]) | 0xff000000u;
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
        public static Vector<byte> Pixels(Vector<byte> b, Vector<byte> s) => Vector.AddSaturate(b, s);
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
