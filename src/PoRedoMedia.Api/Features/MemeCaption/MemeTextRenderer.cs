using SkiaSharp;

namespace PoRedoMedia.Api.Features.MemeCaption;

/// <summary>
/// Shared font-matching and stroke-aware text rendering for meme generation and templates.
/// </summary>
internal static class MemeTextRenderer
{
    public static void DrawText(
        SKCanvas canvas,
        string text,
        SKPoint origin,
        float maxWidth,
        float maxFontSize,
        float minFontSize,
        SKTextAlign alignment)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        using var font = new SKFont(TextLayout.Bold, Math.Max(maxFontSize, minFontSize));
        float strokeWidth;
        List<string> lines;

        // Iteratively shrink the font until the widest wrapped line, outline included, fits
        while (true)
        {
            strokeWidth = Math.Max(font.Size / 8f, 1.5f);
            (lines, var widest) = TextLayout.Wrap(text, font, maxWidth);
            if (font.Size <= minFontSize || widest + (strokeWidth * 2f) <= maxWidth) break;
            font.Size = Math.Max(minFontSize, font.Size - Math.Max(2f, font.Size * 0.08f));
        }

        using var outline = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth,
            StrokeJoin = SKStrokeJoin.Round,
        };
        using var fill = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };

        // The block hangs from the origin; each line lines up the same way the block does.
        var baseline = origin.Y - font.Metrics.Ascent;
        foreach (var line in lines)
        {
            // Outline first, then the fill on top. Drawn the other way round the outline is
            // painted over the fill and eats into the letters.
            canvas.DrawText(line, origin.X, baseline, alignment, font, outline);
            canvas.DrawText(line, origin.X, baseline, alignment, font, fill);
            baseline += font.Spacing;
        }
    }
}
