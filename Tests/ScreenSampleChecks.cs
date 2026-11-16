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
    private static void CheckScreenSamples()
    {
        var defaults = VisualizerSettings.ReadSettings("{}");
        Check(!defaults.ScreenSamplesEnabled && defaults.SampleReaction == AudioReaction.Low,
            "Existing settings keep screen capture disabled with bass as its default beat source");
        var invalid = VisualizerSettings.ReadSettings("""
            {"SampleBlend":999,"SampleReaction":999,"SampleBeats":0,"SampleOpacity":190,"SampleZoom":-4,"SampleFade":5000}
            """);
        Check(invalid.SampleBlend == ScreenSampleBlend.Additive && invalid.SampleReaction == AudioReaction.Focus
            && invalid.SampleBeats == 1 && invalid.SampleOpacity == 100 && invalid.SampleZoom == 0 && invalid.SampleFade == 2000,
            "Screen sample settings validate enums and clamp persisted limits");
        var settings = Quiet with { ScreenSamplesEnabled = true, SampleReaction = AudioReaction.Full,
            SampleBeats = 2, SampleOpacity = 60, SampleZoom = 4, SampleFade = 700 };
        Check(VisualizerSettings.ReadSettings(JsonSerializer.Serialize(settings)) == settings,
            "Screen sample preferences round-trip without persisting screenshot data");
        Check(VisualPresets.All.All(preset => preset.Apply(settings).ScreenSamplesEnabled
            && preset.Apply(settings).SampleBeats == 2 && preset.Apply(settings).SampleOpacity == 60),
            "Visual presets preserve screen sample preferences");

        long now = Stopwatch.GetTimestamp();
        long Tick(double seconds) => now + (long)(seconds * Stopwatch.Frequency);
        var playback = new ScreenSamplePlayback();
        playback.ApplySettings(settings);
        long initial = playback.Request.Generation;
        playback.Update(MotionFrame(now), now);
        long first = playback.Request.Generation;
        Check(first == initial + 1 && playback.Request.Opacity(now) > 0,
            "The first fresh beat captures and shows a sample even with every other visual disabled");
        playback.Update(MotionFrame(now), Tick(0.02));
        Check(playback.Request.Generation == first, "Repeated render frames never count the same beat twice");
        playback.Update(MotionFrame(Tick(0.5)), Tick(0.5));
        Check(playback.Request.Generation == first && playback.Request.Opacity(Tick(0.5)) > 0.3,
            "The held screenshot pulses again on an intermediate beat");
        playback.Update(MotionFrame(Tick(1)), Tick(1));
        Check(playback.Request.Generation == first + 1, "Every two beats jumps to the next screenshot");
        var request = playback.Request;
        Check(request.Opacity(Tick(1.2)) < request.Opacity(Tick(1)) && request.Opacity(Tick(2)) == 0,
            "Screen sample opacity decays and clears completely during silence");
        playback.Update(MotionFrame(now), Tick(2));
        Check(playback.Request == request, "Stale audio cannot retrigger old screen samples");
        playback.Update(MotionFrame(Tick(3)), Tick(2));
        Check(playback.Request == request, "Future audio timestamps cannot trigger a screenshot");
        playback.ApplySettings(settings with { SampleReaction = AudioReaction.High });
        var lowOnly = MotionFrame(Tick(3)) with { High = AudioSignal.Silent };
        long before = playback.Request.Generation;
        playback.Update(lowOnly, Tick(3));
        Check(playback.Request.Generation == before && playback.Request.Opacity(Tick(3)) == 0,
            "The sample beat source remains independent of other frequency ranges");
        playback.ApplySettings(settings with { SampleReaction = AudioReaction.Focus, Focus = FrequencyFocus.Bass });
        playback.Update(MotionFrame(Tick(4)), Tick(4));
        Check(playback.Request.Opacity(Tick(4)) == 0, "Follow focus rejects audio from an outdated frequency focus");
        playback.ApplySettings(settings with { ScreenSamplesEnabled = false });
        playback.Update(MotionFrame(Tick(5)), Tick(5));
        Check(playback.Request.Opacity(Tick(5)) == 0, "Disabling screen samples clears their visibility immediately");

        foreach (var (effect, expected) in new[]
        {
            (ScreenSampleBlend.Normal, 160), (ScreenSampleBlend.Additive, 255), (ScreenSampleBlend.Screen, 198),
            (ScreenSampleBlend.Multiply, 64), (ScreenSampleBlend.Difference, 58), (ScreenSampleBlend.Negative, 95)
        })
            Check(BeatScreenSampler.BlendChannel(102, 160, effect) == expected, $"{effect} uses the expected channel blend");
        byte[] background = [102, 102, 102, 0, 10, 20, 30, 0];
        byte[] held = [160, 160, 160, 0, 90, 80, 70, 0];
        byte[] pixels = new byte[8];
        BeatScreenSampler.BlendPixels(background, held, pixels, 2, 1, ScreenSampleBlend.Difference, 1);
        Check(pixels.SequenceEqual(new byte[] { 58, 58, 58, 255, 80, 60, 40, 255 }),
            "Blending combines the held sample with the current desktop using opaque BGRA pixels");
        byte[] zoomSample = Enumerable.Range(1, 5).SelectMany(value => new byte[] { (byte)(value * 10), 0, 0, 255 }).ToArray();
        byte[] zoomOutput = new byte[zoomSample.Length];
        BeatScreenSampler.BlendPixels(new byte[zoomSample.Length], zoomSample, zoomOutput, 5, 1, ScreenSampleBlend.Normal, 2);
        Check(Enumerable.Range(0, 5).Select(index => zoomOutput[index * 4]).SequenceEqual(new byte[] { 20, 20, 30, 30, 40 }),
            "Beat zoom crops the held screenshot around its center without reading outside the image");
    }
}
