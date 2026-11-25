using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Video.TacticalAi;

/// <summary>
/// 2D Spatial Influence Map (16x9 grid, &lt;0.05 ms) for tactical sticker, caption, and comic bubble placement.
/// Combines Florence-2 subject boxes, caption safe-zones, and prior sticker repulsion so overlays flank
/// subjects cleanly without overlapping faces or text bands.
/// </summary>
public sealed class SpatialInfluenceMap
{
    public const int Width = 16;
    public const int Height = 9;
    private readonly float[] _cells = new float[Width * Height];

    /// <summary>Initializes the map with caption safe-zone penalties (top &amp; bottom bands).</summary>
    public SpatialInfluenceMap()
    {
        for (var y = 0; y < Height; y++)
        {
            var normY = (y + 0.5f) / Height;
            var penalty = normY is < 0.16f or > 0.82f ? -0.65f : 0.1f;
            for (var x = 0; x < Width; x++)
                _cells[y * Width + x] = penalty;
        }
    }

    /// <summary>Adds positive or negative Gaussian influence around normalized (<paramref name="cx"/>, <paramref name="cy"/>).</summary>
    public void Stamp(double cx, double cy, float weight, float radius = 0.22f)
    {
        var r2 = MathF.Max(0.002f, radius * radius);
        for (var y = 0; y < Height; y++)
        {
            var ny = (y + 0.5f) / Height;
            var dy = ny - (float)cy;
            for (var x = 0; x < Width; x++)
            {
                var nx = (x + 0.5f) / Width;
                var dx = nx - (float)cx;
                var falloff = MathF.Exp(-(dx * dx + dy * dy) / r2);
                _cells[y * Width + x] += weight * falloff;
            }
        }
    }

    /// <summary>Applies Florence-2 detected boxes: flank mode avoids covering faces while staying adjacent.</summary>
    public void ApplyVisionBoxes(IReadOnlyList<VisionBoxDto> boxes, bool flankSubject)
    {
        foreach (var b in boxes)
        {
            var r = (float)Math.Clamp(Math.Max(b.W, b.H) * 0.65, 0.12, 0.45);
            if (flankSubject)
            {
                // Repel direct center overlap, attract flanking positions left/right of subject.
                Stamp(b.CenterX, b.CenterY, -0.9f, r * 0.7f);
                Stamp(Math.Clamp(b.X - 0.08, 0.1, 0.9), b.CenterY, +0.65f, r);
                Stamp(Math.Clamp(b.X + b.W + 0.08, 0.1, 0.9), b.CenterY, +0.65f, r);
            }
            else
            {
                Stamp(b.CenterX, b.CenterY, +0.95f, r);
            }
        }
    }

    /// <summary>Picks the highest-utility normalized coordinate and stamps repulsion so the next sticker spreads out.</summary>
    public (double X, double Y) PickAndReserve()
    {
        var bestIdx = 0;
        var bestVal = float.NegativeInfinity;
        for (var i = 0; i < _cells.Length; i++)
        {
            if (_cells[i] > bestVal)
            {
                bestVal = _cells[i];
                bestIdx = i;
            }
        }

        var bx = ((bestIdx % Width) + 0.5) / Width;
        var by = ((bestIdx / Width) + 0.5) / Height;
        Stamp(bx, by, -1.1f, 0.2f);
        return (Math.Clamp(bx, 0.08, 0.92), Math.Clamp(by, 0.12, 0.88));
    }
}
