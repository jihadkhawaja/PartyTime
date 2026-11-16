using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PartyTime.Audio;
using PartyTime.Settings;
using PartyTime.Views;
using PartyTime.Visualization;
using BitmapSource = Avalonia.Media.Imaging.Bitmap;

internal static partial class Program
{
    private static VisualizerSettings Quiet => new()
    {
        Style = VisualStyle.None, Circle = CircleStyle.None, Border = BorderStyle.None, Overlay = OverlayStyle.None
    };

    private static AudioFrame MotionFrame(long now)
    {
        AudioSignal Signal(FrequencyRange range) => new(0.65f, 0.6f,
            Enumerable.Range(0, AudioAnalyzer.BandCount).Select(i => AudioAnalyzer.IncludesBand(i, range) ? 0.6f : 0).ToArray(),
            now - Stopwatch.Frequency / 20, 0.8f, range);
        return new(now, Signal(FrequencyRange.Full), Signal(FrequencyRange.Full),
            Signal(FrequencyRange.Bass), Signal(FrequencyRange.Mids), Signal(FrequencyRange.Treble));
    }

    private static void CheckMotion()
    {
        Check(FramePacing.CursorRate(60) == 60 && FramePacing.CursorRate(144) == 120 && FramePacing.CursorRate(59.94) == 59.94
            && FramePacing.CursorRate(null) == 60, "Cursor cadence follows refresh rate independently and preserves fractional refresh rates");
        long period = FramePacing.Period(120);
        long advanced = FramePacing.Advance(100, 100 + period * 4 + period / 2, period);
        Check(advanced == 100 + period * 5, "Missed cursor frames advance directly to the next deadline");
        long now = Stopwatch.GetTimestamp();
        var audio = MotionFrame(now);
        foreach (var shape in Enum.GetValues<ShapeKind>())
        {
            var scene = new Scene();
            scene.ApplySettings(Quiet with { ShapesEnabled = true, Shape = shape, ShapeCount = 10, ShapesReaction = AudioReaction.Full });
            Check(scene.Update(audio, now, 0.1, null), $"{shape} activates as an independent shape layer");
            var drawing = new RecordingDrawing();
            scene.Draw(drawing, 800, 600);
            Check(drawing.Shapes == 10 && drawing.PolygonVertices.SelectMany(points => points).All(point => double.IsFinite(point.X) && double.IsFinite(point.Y)),
                $"{shape} renders the chosen count with finite geometry");
        }
        foreach (var motion in Enum.GetValues<ShapeMotion>())
        {
            var scene = new Scene();
            scene.ApplySettings(Quiet with { ShapesEnabled = true, Shape = ShapeKind.Hexagon, ShapeMotion = motion, ShapeCount = 12, FilledShapes = true });
            scene.Update(audio, now, 0.1, null);
            var drawing = new RecordingDrawing();
            scene.Draw(drawing, 800, 600);
            Check(drawing.FilledPolygons == 12, $"{motion} animates the filled shape layer");
        }
        var focused = new Scene();
        focused.ApplySettings(Quiet with { ShapesEnabled = true, ShapesReaction = AudioReaction.High });
        var low = Tone(100);
        Check(!focused.Update(low, low.Timestamp, 0.1, null), "High-routed shapes ignore bass-only audio");

        foreach (var style in Enum.GetValues<CursorTrailStyle>())
        {
            var settings = Quiet with { CursorTrailEnabled = true, CursorTrail = style, CursorShape = ShapeKind.Circle };
            var scene = new Scene();
            scene.ApplySettings(settings);
            scene.UpdatePointer(new Point(-1800, 150), now);
            Check(scene.Update(AudioFrame.Silent, now, 0.1, null), $"{style} cursor trail works during silence");
            var drawing = new RecordingDrawing();
            scene.Draw(drawing, 800, 600, new Point(-1920, 0), 1.5, 1.5);
            Check(drawing.Centers.Count > 0 && ((Vector)(drawing.Centers[0] - new Point(80, 100))).Length < 0.001,
                $"{style} cursor coordinates account for negative monitor origins and DPI");
            drawing = new RecordingDrawing();
            scene.Draw(drawing, 800, 600);
            Check(drawing.Shapes == 0, $"{style} cursor trail is not mirrored onto another display");
            long later = now + Stopwatch.Frequency * 2;
            scene.UpdatePointer(new Point(-1800, 150), later);
            Check(!scene.Update(AudioFrame.Silent, later, 0.2, null), $"{style} cursor trail fades when the mouse stops");
            scene.UpdatePointer(new Point(-1750, 160), later);
            scene.ApplySettings(settings with { CursorTrailEnabled = false });
            scene.ApplySettings(settings);
            Check(!scene.Update(AudioFrame.Silent, later, 0.1, null), $"{style} toggle clears previous cursor history");
        }
        var bounded = new Scene();
        bounded.ApplySettings(Quiet with { CursorTrailEnabled = true, CursorTrailLength = 2000 });
        for (int i = 0; i < 500; i++)
            bounded.UpdatePointer(new Point(i * 9, 150), now + i * Stopwatch.Frequency / 1000);
        bounded.Update(AudioFrame.Silent, now + Stopwatch.Frequency / 2, 0.1, null);
        var boundedDrawing = new RecordingDrawing();
        bounded.Draw(boundedDrawing, 10000, 1000);
        Check(boundedDrawing.Shapes is > 0 and <= 384, "Cursor history stays bounded during fast movement");
        Check(bounded.CursorIntersects(new Rect(0, 0, 5000, 1000)) && !bounded.CursorIntersects(new Rect(-2000, 0, 500, 1000)),
            "Cursor repainting is restricted to displays touched by the trail");
        Check(bounded.HasCursorTrail && !bounded.BaseVisible, "Cursor-only motion keeps the music layer inactive");
    }

}
