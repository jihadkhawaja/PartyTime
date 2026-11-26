using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;
using PartyTime.Audio;
using PartyTime.Controls;
using PartyTime.Settings;
using PartyTime.Visualization;

internal static partial class Program
{
    private static void CheckImmersion()
    {
        var settings = Quiet with { MusicEffect = MusicEffect.SpectrumWaterfall, MusicReaction = AudioReaction.High,
            MusicStrength = 84, AmbientLighting = true, AmbientScreenColors = true, CinemaVignette = true, CinemaStrength = 72 };
        Check(VisualizerSettings.ReadSettings(JsonSerializer.Serialize(settings)) == settings,
            "Immersion preferences survive a settings round trip");
        var legacy = VisualizerSettings.ReadSettings("{}");
        var invalid = VisualizerSettings.ReadSettings("{\"MusicEffect\":19,\"MusicReaction\":-1,\"MusicStrength\":101,\"CinemaStrength\":-1}");
        Check(legacy.MusicEffect == MusicEffect.None && !legacy.AmbientLighting && !legacy.CinemaVignette
            && invalid.MusicEffect == MusicEffect.None && invalid.MusicReaction == AudioReaction.Full
            && invalid.MusicStrength == 100 && invalid.CinemaStrength == 0,
            "Old settings keep immersion off and invalid values return to supported limits");

        var scene = new Scene();
        var tone = Tone(100);
        scene.ApplySettings(Quiet with { MusicEffect = MusicEffect.StageBeams, MusicReaction = AudioReaction.High });
        Check(!scene.Update(tone, tone.Timestamp, 0.1, null), "Music visuals stay hidden when their selected range is silent");
        scene.ApplySettings(Quiet with { MusicEffect = MusicEffect.StageBeams, MusicReaction = AudioReaction.Low });
        Check(scene.Update(tone, tone.Timestamp, 0.1, null), "Music visuals can follow bass independently");

        long start = Stopwatch.GetTimestamp();
        scene.ApplySettings(Quiet with { MusicEffect = MusicEffect.SpectrumWaterfall, Intensity = 100, MusicStrength = 100 });
        scene.Update(MotionFrame(start), start, 0.01, null);
        using var overlay = new OverlaySurface();
        byte[] Draw(Color[]? colors = null)
        {
            overlay.ShowScene(scene, ambientColors: colors);
            using var target = Render(overlay, 360, 200);
            return BitmapPixels.Read(target);
        }
        int PixelsWithAlpha(byte[] pixels) => pixels.Where((value, index) => index % 4 == 3 && value != 0).Count();
        int front = PixelsWithAlpha(Draw());
        for (int i = 1; i <= 18; i++)
        {
            long now = start + i * Stopwatch.Frequency / 10;
            scene.Update(MotionFrame(now), now, 0.1, null);
        }
        int history = PixelsWithAlpha(Draw());
        Check(front > 0 && history > front * 3, "The waterfall draws recent spectrum frames behind its live ridge");
        scene.ApplySettings(Quiet with { MusicEffect = MusicEffect.SpectrumWaterfall, Intensity = 100, MusicStrength = 100,
            MusicReaction = AudioReaction.Low });
        Check(PixelsWithAlpha(Draw()) < history / 2, "Changing the waterfall's audio range clears its history");

        scene.ApplySettings(settings with { MusicEffect = MusicEffect.None, Intensity = 100, CinemaStrength = 100 });
        long cinemaTime = start + Stopwatch.Frequency * 2;
        scene.Update(MotionFrame(cinemaTime), cinemaTime, 0.1, null);
        Color[] firstDisplay = [Colors.Red, Colors.Lime, Colors.Blue, Colors.Yellow];
        Color[] secondDisplay = [Colors.Blue, Colors.Red, Colors.Lime, Colors.Yellow];
        var first = Draw(firstDisplay);
        var second = Draw(secondDisplay);
        var fallback = Draw();
        const int center = (100 * 360 + 180) * 4;
        Check(first[center + 3] == 0 && second[center + 3] == 0 && first[3] > 0,
            "Cinema lighting stays outside the center of the screen");
        Check(first[2] > first[0] && second[0] > second[2] && !fallback.SequenceEqual(second),
            "The scene renders each display's own corner colors and falls back to the palette");
        var transition = new AmbientColorTransition();
        transition.Update(firstDisplay, 0.01);
        transition.Update(secondDisplay, 0.1);
        Check(transition.Colors![0].R is > 0 and < 255 && transition.Colors[0].B > 0,
            "Changing desktop colors blends between samples");
        transition.Update(null, 0.1);
        Check(transition.Colors == null, "Unavailable desktop colors reset the transition to palette fallback");
        for (int i = 1; i <= 14; i++)
            scene.Update(AudioFrame.Silent, cinemaTime + i * Stopwatch.Frequency / 10, 0.1, null);
        Check(!scene.BaseVisible && Draw(firstDisplay).All(value => value == 0), "Immersion layers fade out and clear when playback stops");

        var preview = new StylePreview();
        preview.ApplySettings(Quiet with { MusicEffect = MusicEffect.WarpTunnel, MusicStrength = 100, Intensity = 100 });
        using var demo = Render(preview, 360, 200);
        preview.SetLiveAudio(() => AudioFrame.Silent);
        using var silent = Render(preview, 360, 200);
        preview.SetLiveAudio(null);
        using var restored = Render(preview, 360, 200);
        var silentPixels = BitmapPixels.Read(silent);
        Check(!BitmapPixels.Read(demo).SequenceEqual(silentPixels) && !BitmapPixels.Read(restored).SequenceEqual(silentPixels),
            "Switching to live preview clears demo motion and switching back restores it");
    }
}
