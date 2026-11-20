using System;

namespace PartyTime.Visualization;

internal static class ScreenSampleFilters
{
    public static void Blur(ReadOnlySpan<byte> pixels, Span<byte> output, Span<byte> scratch, int width, int height, int strength)
    {
        int radius = Math.Max(1, (int)Math.Round(Math.Min(width, height) * strength / 6000.0));
        // Two separable box passes soften the edges and take constant work per pixel.
        BlurAxis(pixels, scratch, width, height, 4, width * 4, radius);
        BlurAxis(scratch, output, height, width, width * 4, 4, radius);
        BlurAxis(output, scratch, width, height, 4, width * 4, radius);
        BlurAxis(scratch, output, height, width, width * 4, 4, radius);
    }

    private static void BlurAxis(ReadOnlySpan<byte> input, Span<byte> output, int length, int lines, int step, int lineStride, int radius)
    {
        int diameter = radius * 2 + 1;
        for (int line = 0; line < lines; line++)
        {
            int start = line * lineStride;
            int blue = 0, green = 0, red = 0;
            for (int offset = -radius; offset <= radius; offset++)
            {
                int pixel = start + Math.Clamp(offset, 0, length - 1) * step;
                blue += input[pixel];
                green += input[pixel + 1];
                red += input[pixel + 2];
            }
            for (int i = 0; i < length; i++)
            {
                int pixel = start + i * step;
                output[pixel] = (byte)((blue + radius) / diameter);
                output[pixel + 1] = (byte)((green + radius) / diameter);
                output[pixel + 2] = (byte)((red + radius) / diameter);
                output[pixel + 3] = 255;

                int outgoing = start + Math.Max(i - radius, 0) * step;
                int incoming = start + Math.Min(i + radius + 1, length - 1) * step;
                blue += input[incoming] - input[outgoing];
                green += input[incoming + 1] - input[outgoing + 1];
                red += input[incoming + 2] - input[outgoing + 2];
            }
        }
    }

    public static void Pixelate(ReadOnlySpan<byte> pixels, Span<byte> output, int width, int height, int strength)
    {
        int block = Math.Max(2, (int)Math.Round(Math.Min(width, height) * strength / 1500.0));
        for (int top = 0; top < height; top += block)
        for (int left = 0; left < width; left += block)
        {
            int bottom = Math.Min(top + block, height), right = Math.Min(left + block, width);
            int blue = 0, green = 0, red = 0;
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                int pixel = (y * width + x) * 4;
                blue += pixels[pixel];
                green += pixels[pixel + 1];
                red += pixels[pixel + 2];
            }

            int count = (bottom - top) * (right - left);
            byte b = (byte)(blue / count), g = (byte)(green / count), r = (byte)(red / count);
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                int pixel = (y * width + x) * 4;
                output[pixel] = b;
                output[pixel + 1] = g;
                output[pixel + 2] = r;
                output[pixel + 3] = 255;
            }
        }
    }
}
