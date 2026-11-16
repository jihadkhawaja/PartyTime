using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using BitmapSource = Avalonia.Media.Imaging.Bitmap;
using PartyTime.Settings;

namespace PartyTime.Visualization;

internal sealed partial class Scene
{
    private sealed record ImageSprite(OverlayImageSettings Settings, BitmapSource Bitmap);
    private sealed record ImageGroup(ImageSprite[] Slots, int X, int Y);

    private ImageSprite[] _images = [];
    private BitmapSource[] _imageSources = [];
    private ImageGroup[] _stickerGroups = [];
    private ImageGroup[] _haloGroups = [];
    private ImageSprite[] _floatingSlots = [];
    private bool HasDotReplacement => _images.Any(image => image.Settings.Layout == ImageLayout.ReplaceDots);

    private void ApplyImages(OverlayImageSettings[] images)
    {
        _images = images.Where(image => image.Enabled).Select(image => (Settings: image, Bitmap: OverlayImageStore.Load(image)))
            .Where(image => image.Bitmap != null).Select(image => new ImageSprite(image.Settings, image.Bitmap!)).ToArray();
        _imageSources = _images.Select(image => image.Bitmap).Distinct().ToArray();
        _stickerGroups = _images.Where(image => image.Settings.Layout == ImageLayout.Sticker)
            .GroupBy(image => (image.Settings.X, image.Settings.Y))
            .Select(group => new ImageGroup(group.ToArray(), group.Key.X, group.Key.Y)).ToArray();
        _haloGroups = _images.Where(image => image.Settings.Layout == ImageLayout.Halo
                || (image.Settings.Layout == ImageLayout.ReplaceDots && _settings.Circle == CircleStyle.DotHalo))
            .GroupBy(image => image.Settings.Layout == ImageLayout.ReplaceDots ? (X: 50, Y: 50) : (image.Settings.X, image.Settings.Y))
            .Select(group => new ImageGroup(Interleave(group.ToArray()), group.Key.X, group.Key.Y)).ToArray();
        _floatingSlots = Interleave(_images.Where(image => image.Settings.Layout == ImageLayout.Scatter
            || (image.Settings.Layout == ImageLayout.ReplaceDots && _settings.Overlay == OverlayStyle.Sparks)).ToArray());
    }

    private static ImageSprite[] Interleave(ImageSprite[] images)
    {
        var slots = new List<ImageSprite>();
        for (int copy = 0; images.Any(image => copy < image.Settings.Count); copy++)
            foreach (var image in images)
                if (copy < image.Settings.Count)
                    slots.Add(image);
        return slots.ToArray();
    }

    private void DrawImages(ISceneDrawing drawing, double width, double height, double shortSide)
    {
        foreach (var group in _stickerGroups)
            DrawStickers(drawing, group, width, height, shortSide);
        foreach (var group in _haloGroups)
            DrawImageHalo(drawing, group, width, height, shortSide);
        DrawFloatingImages(drawing, width, height, shortSide);
    }

    private void DrawStickers(ISceneDrawing drawing, ImageGroup group, double width, double height, double shortSide)
    {
        int columns = GridColumns(group.Slots.Length, width, height);
        int rows = (group.Slots.Length + columns - 1) / columns;
        double gap = shortSide * 0.015;
        double cell = Math.Min(shortSide * group.Slots.Max(image => image.Settings.Size) / 100 * 1.4 + gap,
            Math.Min(width * 0.9 / columns, height * 0.9 / rows));
        double left = Math.Clamp(width * group.X / 100 - columns * cell / 2, width * 0.05, Math.Max(width * 0.05, width * 0.95 - columns * cell));
        double top = Math.Clamp(height * group.Y / 100 - rows * cell / 2, height * 0.05, Math.Max(height * 0.05, height * 0.95 - rows * cell));
        for (int slot = 0; slot < group.Slots.Length; slot++)
        {
            var (settings, bitmap) = group.Slots[slot];
            if (!SelectReaction(settings.Reaction))
                continue;
            var center = group.Slots.Length == 1 ? new Point(width * group.X / 100, height * group.Y / 100)
                : new Point(left + (slot % columns + 0.5) * cell, top + (slot / columns + 0.5) * cell);
            double size = ImageSize(settings, shortSide);
            if (group.Slots.Length > 1)
                size = Math.Min(size, cell - Math.Min(gap, cell * 0.1));
            DrawImageAt(drawing, bitmap, center, size, ImageOpacity(settings));
        }
    }

    private void DrawImageHalo(ISceneDrawing drawing, ImageGroup group, double width, double height, double shortSide)
    {
        var center = new Point(width * group.X / 100, height * group.Y / 100);
        // Reserve enough space even when neighboring images react to different ranges.
        double minimumRadius = shortSide * Size * 0.15;
        double slotSize = group.Slots.Length == 1 ? double.PositiveInfinity
            : 2 * minimumRadius * Math.Sin(Math.PI / group.Slots.Length) / Math.Sqrt(2) * 0.9;
        for (int slot = 0; slot < group.Slots.Length; slot++)
        {
            var (settings, bitmap) = group.Slots[slot];
            if (!SelectReaction(settings.Reaction))
                continue;
            double band = _bands[slot * _bands.Length / group.Slots.Length];
            double angle = Math.Tau * slot / group.Slots.Length - Math.PI / 2;
            double radius = shortSide * Size * (0.15 + _drive * 0.03 + _pulse * 0.05 + band * 0.09);
            var point = new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
            DrawImageAt(drawing, bitmap, point, Math.Min(ImageSize(settings, shortSide) * (0.6 + band * 0.4), slotSize), ImageOpacity(settings));
        }
    }

    private void DrawFloatingImages(ISceneDrawing drawing, double width, double height, double shortSide)
    {
        if (_floatingSlots.Length == 0)
            return;
        int columns = GridColumns(_floatingSlots.Length, width, height);
        int rows = (_floatingSlots.Length + columns - 1) / columns;
        double cellWidth = width * 0.86 / columns;
        double cellHeight = height * 0.86 / rows;
        double slotSize = Math.Min(cellWidth, cellHeight) * 0.6;
        for (int slot = 0; slot < _floatingSlots.Length; slot++)
        {
            var (settings, bitmap) = _floatingSlots[slot];
            if (!SelectReaction(settings.Reaction))
                continue;
            var (sin, cos) = Math.SinCos(Math.Tau * _phase / 4 + (slot + 1) * 2.4);
            var point = new Point(width * 0.07 + (slot % columns + 0.5 + cos * 0.15) * cellWidth,
                height * 0.07 + (slot / columns + 0.5 + sin * 0.15) * cellHeight);
            DrawImageAt(drawing, bitmap, point, Math.Min(ImageSize(settings, shortSide) * (0.8 + 0.2 * sin), slotSize), ImageOpacity(settings));
        }
    }

    private static int GridColumns(int count, double width, double height)
        => Math.Clamp((int)Math.Ceiling(Math.Sqrt(count * width / height)), 1, count);

    private double ImageSize(OverlayImageSettings settings, double shortSide)
        => shortSide * settings.Size / 100 * (0.65 + _drive * 0.35 + _pulse * 0.4);

    private double ImageOpacity(OverlayImageSettings settings)
        => settings.Opacity / 100.0 * Intensity * Math.Min(1, _level * 1.2 + _pulse * 0.6);

    private static void DrawImageAt(ISceneDrawing drawing, BitmapSource bitmap, Point center, double size, double opacity)
    {
        double scale = size / Math.Max(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        double width = bitmap.PixelSize.Width * scale;
        double height = bitmap.PixelSize.Height * scale;
        drawing.DrawImage(bitmap, new Rect(center.X - width / 2, center.Y - height / 2, width, height), opacity);
    }
}
