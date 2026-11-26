using System;
using System.Numerics;
using Avalonia.Media;

namespace PartyTime.Visualization;

internal sealed class AmbientColorTransition
{
    private readonly Vector3[] _current = new Vector3[4];
    private readonly Color[] _colors = new Color[4];
    public Color[]? Colors { get; private set; }

    public void Update(Color[]? sample, double elapsed)
    {
        if (sample == null)
        {
            Colors = null;
            return;
        }

        float follow = Colors == null ? 1 : (float)(1 - Math.Exp(-elapsed / 0.33));
        for (int i = 0; i < _colors.Length; i++)
        {
            var target = sample[i];
            var color = _current[i] = Vector3.Lerp(_current[i], new Vector3(target.R, target.G, target.B), follow);
            _colors[i] = Color.FromRgb((byte)MathF.Round(color.X), (byte)MathF.Round(color.Y), (byte)MathF.Round(color.Z));
        }
        Colors = _colors;
    }
}
