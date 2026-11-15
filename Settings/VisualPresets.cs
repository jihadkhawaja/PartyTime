namespace PartyTime.Settings;

internal sealed record VisualPreset(string Name, string Description, VisualizerSettings Look)
{
    public VisualizerSettings Apply(VisualizerSettings current) => current with
    {
        Style = Look.Style, Circle = Look.Circle, Border = Look.Border, Overlay = Look.Overlay,
        Theme = Look.Theme, ColorSource = ColorSource.Palette, AnimateColors = true,
        Intensity = Look.Intensity, Size = Look.Size,
        BarsReaction = Look.BarsReaction, CircleReaction = Look.CircleReaction,
        BorderReaction = Look.BorderReaction, EffectsReaction = Look.EffectsReaction,
        ShapesEnabled = Look.ShapesEnabled, Shape = Look.Shape, ShapeMotion = Look.ShapeMotion,
        FilledShapes = Look.FilledShapes, ShapeCount = Look.ShapeCount, ShapeSize = Look.ShapeSize,
        ShapeOpacity = Look.ShapeOpacity, ShapesReaction = Look.ShapesReaction,
        CursorTrailEnabled = Look.CursorTrailEnabled, CursorTrail = Look.CursorTrail,
        CursorShape = Look.CursorShape, CursorTrailLength = Look.CursorTrailLength,
        CursorTrailSize = Look.CursorTrailSize, CursorReaction = Look.CursorReaction
    };
}

internal static class VisualPresets
{
    public static VisualPreset[] All { get; } =
    [
        new("Neon party", "Bass-driven orbit, edge bars, and bright geometric confetti.", new()
        {
            Circle = CircleStyle.Orbit, Overlay = OverlayStyle.Sparks, CircleReaction = AudioReaction.Low,
            ShapesEnabled = true, Shape = ShapeKind.Mixed, ShapeCount = 18, ShapeSize = 2, ShapeOpacity = 45
        }),
        new("Geometry dance", "Orbiting hexagons, a spectrum wave, and a ribbon cursor trail.", new()
        {
            Style = VisualStyle.Wave, Circle = CircleStyle.None, Border = BorderStyle.DoubleRail, Overlay = OverlayStyle.None,
            ShapesEnabled = true, Shape = ShapeKind.Hexagon, ShapeMotion = ShapeMotion.Orbit, ShapeCount = 12,
            CursorTrailEnabled = true, CursorTrail = CursorTrailStyle.Ribbon
        }),
        new("Stardust", "Floating stars, a dot halo, and a trail of stars behind your cursor.", new()
        {
            Style = VisualStyle.None, Circle = CircleStyle.DotHalo, Border = BorderStyle.None, Overlay = OverlayStyle.Sparks,
            ShapesEnabled = true, Shape = ShapeKind.Star, ShapeCount = 32, ShapeSize = 2, FilledShapes = true,
            CursorTrailEnabled = true, CursorTrail = CursorTrailStyle.Shapes, CursorShape = ShapeKind.Star
        }),
        new("Heartbeat", "Warm pulse rings and hearts that burst outward on bass hits.", new()
        {
            Style = VisualStyle.None, Circle = CircleStyle.PulseRings, Border = BorderStyle.Corners, Overlay = OverlayStyle.BeatRipple,
            Theme = ColorTheme.Sunset, CircleReaction = AudioReaction.Low, ShapesEnabled = true,
            Shape = ShapeKind.Heart, ShapeMotion = ShapeMotion.Burst, ShapesReaction = AudioReaction.Low, ShapeCount = 16, FilledShapes = true
        }),
        new("Aurora rain", "Cool colors, a soft sweep, and a shower of mixed shapes.", new()
        {
            Style = VisualStyle.Wave, Circle = CircleStyle.None, Overlay = OverlayStyle.LightSweep, Theme = ColorTheme.Aurora,
            ShapesEnabled = true, ShapeMotion = ShapeMotion.Rain, ShapeCount = 30, ShapeSize = 2, ShapeOpacity = 55
        }),
        new("Cosmic spiral", "A radial wave, spinning shapes, and a ribbon trail.", new()
        {
            Style = VisualStyle.None, Circle = CircleStyle.RadialWave, Border = BorderStyle.None, Overlay = OverlayStyle.CornerBloom,
            ShapesEnabled = true, ShapeMotion = ShapeMotion.Spiral, ShapeCount = 28, ShapeSize = 2,
            CursorTrailEnabled = true, CursorTrail = CursorTrailStyle.Ribbon, CursorTrailLength = 900
        }),
        new("Minimal glow", "A quiet border glow with shapes and cursor effects turned off.", new()
        {
            Style = VisualStyle.None, Circle = CircleStyle.None, Overlay = OverlayStyle.None, Intensity = 35, Theme = ColorTheme.Aurora
        }),
        new("Cursor comet", "Just a warm glowing cursor trail, including when audio is silent.", new()
        {
            Style = VisualStyle.None, Circle = CircleStyle.None, Border = BorderStyle.None, Overlay = OverlayStyle.None,
            Theme = ColorTheme.Sunset, CursorTrailEnabled = true, CursorTrailLength = 1000, CursorTrailSize = 22
        })
    ];
}
