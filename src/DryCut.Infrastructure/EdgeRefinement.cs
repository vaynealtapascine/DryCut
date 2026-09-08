using DryCut.Domain.Models;

namespace DryCut.Infrastructure;

public static class EdgeRefinement
{
    public static void Apply(Span<float> alpha, int width, int height, RefinementSettings settings, ReadOnlySpan<byte> rgba)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (alpha.Length != checked(width * height) || rgba.Length != checked(width * height * 4))
            throw new ArgumentException("Mask and image dimensions must match.");
        var radius = settings.Preset switch
        {
            RefinementPreset.None => 0,
            RefinementPreset.Soft => 1,
            RefinementPreset.Balanced => 1,
            RefinementPreset.Detailed => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(settings))
        };
        if (radius == 0) return;

        var original = alpha.ToArray();
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                if (original[index] is <= 0.05f or >= 0.95f) continue;
                var sum = 0f;
                var weight = 0f;
                for (var oy = -radius; oy <= radius; oy++)
                    for (var ox = -radius; ox <= radius; ox++)
                    {
                        var nx = Math.Clamp(x + ox, 0, width - 1);
                        var ny = Math.Clamp(y + oy, 0, height - 1);
                        var neighbour = ny * width + nx;
                        var colorDistance = ColorDistance(rgba, index, neighbour);
                        var w = 1f / (1f + colorDistance * (settings.Preset == RefinementPreset.Detailed ? 2f : 1f));
                        sum += original[neighbour] * w;
                        weight += w;
                    }
                alpha[index] = Math.Clamp(sum / weight, 0f, 1f);
            }
    }

    private static float ColorDistance(ReadOnlySpan<byte> rgba, int first, int second)
    {
        var a = first * 4;
        var b = second * 4;
        var dr = rgba[a] - rgba[b];
        var dg = rgba[a + 1] - rgba[b + 1];
        var db = rgba[a + 2] - rgba[b + 2];
        return MathF.Sqrt(dr * dr + dg * dg + db * db) / 441.67295f;
    }
}
