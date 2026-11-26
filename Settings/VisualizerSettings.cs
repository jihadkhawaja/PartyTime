using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Media;
using PartyTime.Audio;

namespace PartyTime.Settings;

internal enum VisualStyle { EdgeBars, Orbit, CenterBars, Wave, None } // Orbit is retained for older settings files.
internal enum CircleStyle { None, Orbit, PulseRings, RadialWave, DotHalo }
internal enum BorderStyle { None, Glow, DoubleRail, Corners, Marquee }
internal enum OverlayStyle { None, BeatRipple, CornerBloom, Sparks, LightSweep }
internal enum FrequencyFocus { Full, Bass, Vocals, Mids, Treble, Custom }
internal enum ColorTheme { Neon, Sunset, Aurora, Custom }
internal enum ColorSource { Palette, Screen, Mix }
internal enum OverlayFrameRate { MatchDisplay = 0, Fps20 = 20, Fps30 = 30, Fps45 = 45, Fps60 = 60 }
internal enum AudioReaction { Focus, Full, Low, Mid, High }
internal enum ImageLayout { Sticker, Halo, Scatter, ReplaceDots }
internal enum ShapeKind { Circle, Square, Triangle, Diamond, Star, Hexagon, Heart, Cross, Pentagon, Octagon, Mixed }
internal enum ShapeMotion { Float, Orbit, Rain, Burst, Spiral }
internal enum CursorTrailStyle { Glow, Ribbon, Shapes }
internal enum MusicEffect { None, SpectrumWaterfall, WarpTunnel, StageBeams }
// Values are saved as numbers. Append new effects to preserve existing settings.
internal enum ScreenSampleBlend { Normal, Additive, Screen, Multiply, Difference, Negative, Blur, ZoomOnly, Pixelate }

internal sealed record OverlayImageSettings
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Image";
    public string Path { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public ImageLayout Layout { get; init; } = ImageLayout.Sticker;
    public AudioReaction Reaction { get; init; } = AudioReaction.Focus;
    public int Size { get; init; } = 10;
    public int Opacity { get; init; } = 85;
    public int Count { get; init; } = 16;
    public int X { get; init; } = 50;
    public int Y { get; init; } = 50;
    public bool ChromaKeyEnabled { get; init; }
    public string ChromaKeyColor { get; init; } = "#00FF00";
    public int ChromaKeyTolerance { get; init; } = 20;
}

internal sealed record VisualizerSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PartyTime", "settings.json");

    public VisualStyle Style { get; init; } = VisualStyle.EdgeBars;
    public CircleStyle Circle { get; init; } = CircleStyle.None;
    public BorderStyle Border { get; init; } = BorderStyle.Glow;
    public OverlayStyle Overlay { get; init; } = OverlayStyle.BeatRipple;
    public int Intensity { get; init; } = 65;
    public int Size { get; init; } = 100;
    public FrequencyFocus Focus { get; init; } = FrequencyFocus.Full;
    public int LowHz { get; init; } = 60;
    public int HighHz { get; init; } = 500;
    public int Response { get; init; } = 100;
    public ColorTheme Theme { get; init; } = ColorTheme.Neon;
    public ColorSource ColorSource { get; init; } = ColorSource.Palette;
    public int ScreenBlend { get; init; } = 60;
    public string FirstColor { get; init; } = "#B45CFF";
    public string SecondColor { get; init; } = "#24DFFF";
    public bool AnimateColors { get; init; } = true;
    public OverlayFrameRate FrameRate { get; init; } = OverlayFrameRate.Fps30;
    public AudioReaction BarsReaction { get; init; } = AudioReaction.Focus;
    public AudioReaction CircleReaction { get; init; } = AudioReaction.Focus;
    public AudioReaction BorderReaction { get; init; } = AudioReaction.Focus;
    public AudioReaction EffectsReaction { get; init; } = AudioReaction.Focus;
    public OverlayImageSettings[] Images { get; init; } = [];
    public bool ShapesEnabled { get; init; }
    public ShapeKind Shape { get; init; } = ShapeKind.Mixed;
    public ShapeMotion ShapeMotion { get; init; } = ShapeMotion.Float;
    public bool FilledShapes { get; init; }
    public int ShapeCount { get; init; } = 24;
    public int ShapeSize { get; init; } = 3;
    public int ShapeOpacity { get; init; } = 65;
    public AudioReaction ShapesReaction { get; init; } = AudioReaction.Mid;
    public bool CursorTrailEnabled { get; init; }
    public CursorTrailStyle CursorTrail { get; init; } = CursorTrailStyle.Glow;
    public ShapeKind CursorShape { get; init; } = ShapeKind.Star;
    public int CursorTrailLength { get; init; } = 650;
    public int CursorTrailSize { get; init; } = 16;
    public AudioReaction CursorReaction { get; init; } = AudioReaction.Full;
    public bool ScreenSamplesEnabled { get; init; }
    public ScreenSampleBlend SampleBlend { get; init; } = ScreenSampleBlend.Additive;
    public AudioReaction SampleReaction { get; init; } = AudioReaction.Low;
    public int SampleBeats { get; init; } = 2;
    public int SampleOpacity { get; init; } = 40;
    public int SampleZoom { get; init; } = 3;
    public int SampleEffectStrength { get; init; } = 50;
    public int SampleFade { get; init; } = 700;
    public MusicEffect MusicEffect { get; init; }
    public AudioReaction MusicReaction { get; init; } = AudioReaction.Full;
    public int MusicStrength { get; init; } = 70;
    public bool AmbientLighting { get; init; }
    public bool AmbientScreenColors { get; init; }
    public bool CinemaVignette { get; init; }
    public int CinemaStrength { get; init; } = 55;

    internal bool UsesAmbientScreenColors => AmbientLighting && AmbientScreenColors;

    public FrequencyRange GetRange() => Focus switch
    {
        FrequencyFocus.Bass => FrequencyRange.Bass,
        FrequencyFocus.Vocals => FrequencyRange.Vocals,
        FrequencyFocus.Mids => FrequencyRange.Mids,
        FrequencyFocus.Treble => FrequencyRange.Treble,
        FrequencyFocus.Custom => new(LowHz, HighHz),
        _ => FrequencyRange.Full
    };

    public static bool TryReadColor(string? text, out Color color)
    {
        color = default;
        var hex = text.AsSpan().Trim();
        if (!hex.IsEmpty && hex[0] == '#')
            hex = hex[1..];
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier,
            System.Globalization.CultureInfo.InvariantCulture, out uint rgb))
            return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    public static string ColorHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static VisualizerSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return ReadSettings(File.ReadAllText(SettingsPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine(ex);
        }
        return new();
    }

    internal static VisualizerSettings ReadSettings(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var defaults = new VisualizerSettings();
        if (root.Deserialize<VisualizerSettings>() is not { } saved)
            return defaults;

        // Older files saved Orbit as the main style and used one toggle for both edge effects.
        if (saved.Style == VisualStyle.Orbit)
        {
            saved = saved with
            {
                Style = VisualStyle.None,
                Circle = root.TryGetProperty(nameof(Circle), out _) ? saved.Circle : CircleStyle.Orbit
            };
        }
        if (root.TryGetProperty("EdgeGlow", out var edgeGlow) && edgeGlow.ValueKind == JsonValueKind.False)
        {
            if (!root.TryGetProperty(nameof(Border), out _))
                saved = saved with { Border = BorderStyle.None };
            if (!root.TryGetProperty(nameof(Overlay), out _))
                saved = saved with { Overlay = OverlayStyle.None };
        }

        bool validRange = FrequencyRange.IsValid(saved.LowHz, saved.HighHz);
        return saved with
        {
            Style = Enum.IsDefined(saved.Style) ? saved.Style : defaults.Style,
            Circle = Enum.IsDefined(saved.Circle) ? saved.Circle : defaults.Circle,
            Border = Enum.IsDefined(saved.Border) ? saved.Border : defaults.Border,
            Overlay = Enum.IsDefined(saved.Overlay) ? saved.Overlay : defaults.Overlay,
            Intensity = Math.Clamp(saved.Intensity, 0, 100),
            Size = Math.Clamp(saved.Size, 60, 150),
            Focus = Enum.IsDefined(saved.Focus) ? saved.Focus : defaults.Focus,
            LowHz = validRange ? saved.LowHz : defaults.LowHz,
            HighHz = validRange ? saved.HighHz : defaults.HighHz,
            Response = Math.Clamp(saved.Response, 50, 175),
            Theme = Enum.IsDefined(saved.Theme) ? saved.Theme : defaults.Theme,
            ColorSource = Enum.IsDefined(saved.ColorSource) ? saved.ColorSource : defaults.ColorSource,
            ScreenBlend = Math.Clamp(saved.ScreenBlend, 0, 100),
            FirstColor = TryReadColor(saved.FirstColor, out var first) ? ColorHex(first) : defaults.FirstColor,
            SecondColor = TryReadColor(saved.SecondColor, out var second) ? ColorHex(second) : defaults.SecondColor,
            FrameRate = Enum.IsDefined(saved.FrameRate) ? saved.FrameRate : defaults.FrameRate,
            BarsReaction = ValidReaction(saved.BarsReaction),
            CircleReaction = ValidReaction(saved.CircleReaction),
            BorderReaction = ValidReaction(saved.BorderReaction),
            EffectsReaction = ValidReaction(saved.EffectsReaction),
            Shape = Enum.IsDefined(saved.Shape) ? saved.Shape : defaults.Shape,
            ShapeMotion = Enum.IsDefined(saved.ShapeMotion) ? saved.ShapeMotion : defaults.ShapeMotion,
            ShapeCount = Math.Clamp(saved.ShapeCount, 4, 96),
            ShapeSize = Math.Clamp(saved.ShapeSize, 1, 12),
            ShapeOpacity = Math.Clamp(saved.ShapeOpacity, 0, 100),
            ShapesReaction = ValidReaction(saved.ShapesReaction),
            CursorTrail = Enum.IsDefined(saved.CursorTrail) ? saved.CursorTrail : defaults.CursorTrail,
            CursorShape = Enum.IsDefined(saved.CursorShape) ? saved.CursorShape : defaults.CursorShape,
            CursorTrailLength = Math.Clamp(saved.CursorTrailLength, 150, 2000),
            CursorTrailSize = Math.Clamp(saved.CursorTrailSize, 4, 40),
            CursorReaction = ValidReaction(saved.CursorReaction),
            SampleBlend = Enum.IsDefined(saved.SampleBlend) ? saved.SampleBlend : defaults.SampleBlend,
            SampleReaction = ValidReaction(saved.SampleReaction),
            SampleBeats = Math.Clamp(saved.SampleBeats, 1, 8),
            SampleOpacity = Math.Clamp(saved.SampleOpacity, 0, 100),
            SampleZoom = Math.Clamp(saved.SampleZoom, 0, 12),
            SampleEffectStrength = Math.Clamp(saved.SampleEffectStrength, 1, 100),
            SampleFade = Math.Clamp(saved.SampleFade, 150, 2000),
            MusicEffect = Enum.IsDefined(saved.MusicEffect) ? saved.MusicEffect : defaults.MusicEffect,
            MusicReaction = Enum.IsDefined(saved.MusicReaction) ? saved.MusicReaction : defaults.MusicReaction,
            MusicStrength = Math.Clamp(saved.MusicStrength, 0, 100),
            CinemaStrength = Math.Clamp(saved.CinemaStrength, 0, 100),
            Images = (saved.Images ?? []).Where(image => image != null).Take(16).Select(image => image with
            {
                Id = Guid.TryParseExact(image.Id, "N", out _) ? image.Id : Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(image.Name) ? "Image" : image.Name,
                Path = image.Path ?? "",
                Layout = Enum.IsDefined(image.Layout) ? image.Layout : ImageLayout.Sticker,
                Reaction = ValidReaction(image.Reaction),
                Size = Math.Clamp(image.Size, 1, 50),
                Opacity = Math.Clamp(image.Opacity, 0, 100),
                Count = Math.Clamp(image.Count, 1, 64),
                X = Math.Clamp(image.X, 0, 100),
                Y = Math.Clamp(image.Y, 0, 100),
                ChromaKeyColor = TryReadColor(image.ChromaKeyColor, out var key) ? ColorHex(key) : "#00FF00",
                ChromaKeyTolerance = Math.Clamp(image.ChromaKeyTolerance, 0, 100)
            }).ToArray()
        };
    }

    private static AudioReaction ValidReaction(AudioReaction reaction)
        => Enum.IsDefined(reaction) ? reaction : AudioReaction.Focus;

    public void Save()
    {
        string temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex); }
        }
    }
}
