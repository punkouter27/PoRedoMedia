using SkiaSharp;

namespace PoRedoMedia.Api.Common;

/// <summary>The font and the word wrap shared by everything that draws words onto a picture (memes, comics).</summary>
internal static class TextLayout
{
    private static readonly Lazy<SKTypeface> Typeface = new(ResolveTypeface);

    /// <summary>The bold face every drawn caption uses: a system font, else the bundled DejaVu Sans Bold.</summary>
    public static SKTypeface Bold => Typeface.Value;

    private static SKTypeface ResolveTypeface()
    {
        // Linux uses Skia's no-dependencies build, which has no fontconfig, so family lookups come
        // back empty there and the font file is used instead.
        foreach (var family in new[] { "Impact", "Liberation Sans", "DejaVu Sans", "Arial", "Helvetica" })
        {
            if (SKFontManager.Default.MatchFamily(family, SKFontStyle.Bold) is { } typeface)
                return typeface;
        }

        foreach (var file in new[] { FFmpegProcess.BundledFontPath, FFmpegProcess.SystemFontPath })
        {
            if (File.Exists(file) && SKTypeface.FromFile(file) is { } typeface)
                return typeface;
        }

        // On the no-dependencies build the default typeface has no glyphs: a meme drawn with it
        // would silently carry no caption, so fail the step instead (a run with no output is refunded).
        return SKTypeface.Default is { GlyphCount: > 0 } fallback
            ? fallback
            : throw new InvalidOperationException($"No font to draw captions with; expected {FFmpegProcess.BundledFontPath}.");
    }

    /// <summary>
    /// Greedy word wrap at <paramref name="maxWidth"/>, and the width of the widest line. A line
    /// break in the text always starts a new line; a word wider than a line on its own is broken
    /// between letters.
    /// </summary>
    public static (List<string> Lines, float Widest) Wrap(string text, SKFont font, float maxWidth)
    {
        var lines = new List<string>();
        var widest = 0f;
        var line = new System.Text.StringBuilder();
        var lineWidth = 0f;
        var space = font.MeasureText(" ");

        void EndLine()
        {
            if (line.Length == 0) return;
            lines.Add(line.ToString());
            widest = Math.Max(widest, lineWidth);
            line.Clear();
            lineWidth = 0;
        }

        foreach (var paragraph in text.Split('\n'))
        {
            foreach (var word in paragraph.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                var wordWidth = font.MeasureText(word);
                if (line.Length > 0 && lineWidth + space + wordWidth <= maxWidth)
                {
                    line.Append(' ').Append(word);
                    lineWidth += space + wordWidth;
                    continue;
                }

                EndLine();
                var rest = word.AsSpan();
                while (font.MeasureText(rest) > maxWidth)
                {
                    var fits = Math.Max(1, font.BreakText(rest, maxWidth));
                    line.Append(rest[..fits]);
                    lineWidth = font.MeasureText(rest[..fits]);
                    EndLine();
                    rest = rest[fits..];
                }

                line.Append(rest);
                lineWidth = font.MeasureText(rest);
            }

            EndLine();
        }

        return (lines, widest);
    }
}
