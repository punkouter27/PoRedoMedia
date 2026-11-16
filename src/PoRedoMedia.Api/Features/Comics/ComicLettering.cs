using SkiaSharp;

namespace PoRedoMedia.Api.Features.Comics;

/// <summary>
/// Letters the comic: the narrator's caption box and the speech bubbles are drawn over each picture here rather than by
/// the picture model, which misspells words. Then the panels are laid out as one strip under the title.
/// </summary>
internal static class ComicLettering
{
    /// <summary>The side of a lettered panel, in pixels. Panels are square.</summary>
    public const int PanelSize = 1024;

    private const float Margin = 26, Pad = 18, Border = 8;
    private static readonly SKColor Ink = SKColors.Black, Paper = new(0xFF, 0xFD, 0xF5), CaptionFill = new(0xFF, 0xE1, 0x4D), NameInk = new(0x55, 0x55, 0x55);

    /// <summary>The picture cropped square, with its caption and bubbles, as a PNG.</summary>
    public static byte[] LetterPanel(byte[] art, ComicPanel panel)
    {
        using var source = ImageBytes.Decode(art);
        using var surface = SKSurface.Create(new SKImageInfo(PanelSize, PanelSize));
        var canvas = surface.Canvas;
        canvas.Clear(Paper);
        DrawCover(canvas, source);

        // Shrink everything together until the lettering leaves the lower ~40% of the picture clear.
        for (var scale = 1f; ; scale -= 0.08f)
        {
            var layout = Layout(panel, scale);
            if (layout.Bottom <= PanelSize * 0.62f || scale <= 0.56f)
            {
                foreach (var block in layout.Blocks)
                    block.Draw(canvas);
                break;
            }
        }

        using var frame = new SKPaint { Color = Ink, Style = SKPaintStyle.Stroke, StrokeWidth = Border * 2, IsAntialias = false };
        canvas.DrawRect(0, 0, PanelSize, PanelSize, frame);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    /// <summary>The lettered panels in rows under a title bar, as a PNG.</summary>
    public static byte[] Strip(IReadOnlyList<byte[]> panels, string title)
    {
        var (columns, rows) = Grid(panels.Count);
        const int cell = 600, gutter = 22, titleBar = 120;
        var width = columns * cell + (columns + 1) * gutter;
        var height = titleBar + rows * cell + (rows + 1) * gutter;
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(Paper);

        using (var font = new SKFont(TextLayout.Bold, 64))
        using (var ink = new SKPaint { Color = Ink, IsAntialias = true })
        {
            while (font.Size > 28 && font.MeasureText(title.ToUpperInvariant()) > width - 2 * gutter)
                font.Size -= 4;
            canvas.DrawText(title.ToUpperInvariant(), width / 2f, titleBar / 2f + font.Size * 0.36f + gutter / 2f, SKTextAlign.Center, font, ink);
        }

        for (var i = 0; i < panels.Count; i++)
        {
            var (row, column) = (i / columns, i % columns);
            var inRow = Math.Min(columns, panels.Count - row * columns);
            var shift = (columns - inRow) * (cell + gutter) / 2f; // a short last row sits in the middle
            using var panel = ImageBytes.Decode(panels[i]);
            var x = shift + gutter + column * (cell + gutter);
            var y = titleBar + gutter + row * (cell + gutter);
            canvas.DrawBitmap(panel, new SKRect(x, y, x + cell, y + cell), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    /// <summary>Columns and rows for a strip of this many panels: 3 in a row, 4 as 2×2, 5–6 as 3×2, 7–8 as 4×2.</summary>
    internal static (int Columns, int Rows) Grid(int count)
    {
        var columns = count switch { <= 3 => Math.Max(count, 1), 4 => 2, <= 6 => 3, _ => 4 };
        return (columns, (count + columns - 1) / columns);
    }

    private static void DrawCover(SKCanvas canvas, SKBitmap source)
    {
        var side = Math.Min(source.Width, source.Height);
        var crop = SKRect.Create((source.Width - side) / 2f, (source.Height - side) / 2f, side, side);
        canvas.DrawBitmap(source, crop, new SKRect(0, 0, PanelSize, PanelSize), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    private sealed record Block(Action<SKCanvas> Draw);

    private sealed record PanelLayout(List<Block> Blocks, float Bottom);

    /// <summary>Where the caption and each bubble go at this text size, top to bottom; each speaker keeps their side.</summary>
    private static PanelLayout Layout(ComicPanel panel, float scale)
    {
        var blocks = new List<Block>();
        var y = Margin + Border;
        var inner = PanelSize - 2 * (Margin + Border);

        if (panel.Caption.Length > 0)
        {
            var size = 30 * scale;
            var (lines, widest) = Wrapped(panel.Caption.ToUpperInvariant(), size, inner * 0.66f - 2 * Pad);
            var box = SKRect.Create(Margin + Border, y, widest + 2 * Pad, lines.Count * size * 1.2f + 2 * Pad * 0.8f);
            var top = y;
            blocks.Add(new Block(canvas =>
            {
                using var fill = new SKPaint { Color = CaptionFill, IsAntialias = true };
                using var edge = new SKPaint { Color = Ink, Style = SKPaintStyle.Stroke, StrokeWidth = 4, IsAntialias = true };
                canvas.DrawRect(box, fill);
                canvas.DrawRect(box, edge);
                DrawLines(canvas, lines, box.Left + Pad, top + Pad * 0.8f, size, Ink, SKTextAlign.Left);
            }));
            y = box.Bottom + 16;
        }

        var speakers = panel.Lines.Select(l => l.Speaker).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var line in panel.Lines)
        {
            var size = 32 * scale;
            var nameSize = 19 * scale;
            var (lines, widest) = Wrapped(line.Text.ToUpperInvariant(), size, inner * 0.62f - 2 * Pad);
            using (var nameFont = new SKFont(TextLayout.Bold, nameSize))
                widest = Math.Max(widest, nameFont.MeasureText(line.Speaker.ToUpperInvariant()));
            var w = widest + 2 * Pad + 8;
            var h = nameSize * 1.3f + lines.Count * size * 1.18f + 2 * Pad;
            var k = speakers.FindIndex(s => s.Equals(line.Speaker, StringComparison.OrdinalIgnoreCase));
            var spread = speakers.Count == 1 ? 0.5f : k / (float)(speakers.Count - 1);
            var left = Margin + Border + spread * (inner - w);
            var bubble = SKRect.Create(left, y, w, h);
            // The tail points down toward where the speaker stands: left to right in speaking order.
            var anchor = Margin + Border + inner * (speakers.Count == 1 ? 0.5f : (k + 0.5f) / speakers.Count);
            var tipX = Math.Clamp(anchor, bubble.Left + 30, bubble.Right - 30);
            var top = y;
            blocks.Add(new Block(canvas =>
            {
                using var round = new SKPathBuilder();
                round.AddRoundRect(new SKRoundRect(bubble, 30, 30));
                using var path = round.Detach();
                using var pointer = new SKPathBuilder();
                pointer.MoveTo(tipX - 18, bubble.Bottom - 6);
                pointer.LineTo(tipX + 18, bubble.Bottom - 6);
                pointer.LineTo(tipX + (anchor < PanelSize / 2f ? -14 : 14), bubble.Bottom + 44);
                pointer.Close();
                using var tail = pointer.Detach();
                using var shape = path.Op(tail, SKPathOp.Union) ?? path;
                using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
                using var edge = new SKPaint { Color = Ink, Style = SKPaintStyle.Stroke, StrokeWidth = 4, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round };
                canvas.DrawPath(shape, fill);
                canvas.DrawPath(shape, edge);
                var centre = bubble.MidX;
                DrawLines(canvas, [line.Speaker.ToUpperInvariant()], centre, top + Pad * 0.8f, nameSize, NameInk, SKTextAlign.Center);
                DrawLines(canvas, lines, centre, top + Pad * 0.8f + nameSize * 1.3f, size, Ink, SKTextAlign.Center);
            }));
            y = bubble.Bottom + 52; // room for the tail
        }

        return new PanelLayout(blocks, y);
    }

    private static (List<string> Lines, float Widest) Wrapped(string text, float size, float maxWidth)
    {
        using var font = new SKFont(TextLayout.Bold, size);
        return TextLayout.Wrap(text, font, maxWidth);
    }

    private static void DrawLines(SKCanvas canvas, IEnumerable<string> lines, float x, float top, float size, SKColor colour, SKTextAlign align)
    {
        using var font = new SKFont(TextLayout.Bold, size);
        using var ink = new SKPaint { Color = colour, IsAntialias = true };
        var baseline = top - font.Metrics.Ascent;
        foreach (var line in lines)
        {
            canvas.DrawText(line, x, baseline, align, font, ink);
            baseline += size * 1.18f;
        }
    }
}
