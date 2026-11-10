using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
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

    private static VisualizerSettings ReadSettings(string json)
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
            FrameRate = Enum.IsDefined(saved.FrameRate) ? saved.FrameRate : defaults.FrameRate
        };
    }

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
