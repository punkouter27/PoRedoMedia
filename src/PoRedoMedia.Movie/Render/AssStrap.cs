using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PoRedoMedia.Movie.Render;

/// <summary>
/// The section strap a clip opens with when it starts a new beat (its name in capitals on a box of the series' accent
/// colour, top left), written as an Advanced SubStation Alpha file, which FFmpeg renders natively. This is the only text
/// burned over a movie's clips: captions of what is said were removed on 2026-11-17 (the owner's call), and so were
/// their presets, sizes and outlines.
/// </summary>
public static partial class AssStrap
{
    private const string Black = "&H00000000";

    /// <summary>How long a section strap stays on screen at the start of its beat.</summary>
    public static readonly TimeSpan Length = TimeSpan.FromSeconds(2.5);

    /// <param name="accent">The series' accent colour, #RRGGBB.</param>
    /// <param name="portrait">An upright frame.</param>
    /// <param name="strap">The beat's name.</param>
    public static string Build(string accent, bool portrait, string strap)
    {
        var name = Clean(strap).Trim();
        // The box lets a little through, so the frosted glass the clip draws behind it shows (see FfmpegArgs.Glass).
        var tinted = "&H50" + AssColour(IsHexColour(accent) ? accent : "#FFD400")[4..];
        var ass = new StringBuilder()
            .AppendLine("[Script Info]")
            .AppendLine("ScriptType: v4.00+")
            .AppendLine($"PlayResX: {(portrait ? 1080 : 1920)}")
            .AppendLine($"PlayResY: {(portrait ? 1920 : 1080)}")
            .AppendLine()
            .AppendLine("[V4+ Styles]")
            .AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding")
            .AppendLine($"Style: Strap,{Fonts.Family("Segoe UI")},{(portrait ? 52 : 44)},{Black},{Black},{tinted},{tinted},-1,0,0,0,100,100,2,0,3,14,0,7,80,80,80,1")
            .AppendLine()
            .AppendLine("[Events]")
            .AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        if (name.Length > 0)
            ass.AppendLine($"Dialogue: 1,{Time(TimeSpan.Zero)},{Time(Length)},Strap,,0,0,0,,{{\\fad(200,300)}}{name.ToUpperInvariant()}");
        return ass.ToString();
    }

    private static string Time(TimeSpan t) => t.ToString(@"h\:mm\:ss\.ff", CultureInfo.InvariantCulture);

    // Braces and backslashes are ASS override syntax; a beat's name must never be able to inject them.
    private static string Clean(string text) => text.Replace("{", "").Replace("}", "").Replace("\\", "");

    private static string AssColour(string hex) => $"&H00{hex[5..7]}{hex[3..5]}{hex[1..3]}".ToUpperInvariant();

    public static bool IsHexColour(string? text) => text is not null && HexColour().IsMatch(text);

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColour();
}
