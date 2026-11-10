namespace PoRedoMedia.Movie.Render;

/// <summary>
/// The one font file every title, strap and caption is drawn with. FFmpeg is handed it by file, never by name: a
/// server has no fonts installed to look a name up in.
/// </summary>
public static class Fonts
{
    /// <summary>A font file the host knows of (one it ships beside itself); tried before the system's own.</summary>
    public static string? Bundled { get; set; }

    /// <summary>The bold font titles are drawn in: the host's own, else the one this system is known to have.</summary>
    /// <exception cref="FileNotFoundException">There is no font to draw with; the message says where one is looked for.</exception>
    public static string Title() =>
        Candidates().FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException("No font was found to draw titles with. Put DejaVuSans-Bold.ttf in a fonts folder beside the app.");

    private static IEnumerable<string> Candidates() =>
    [
        .. Bundled is { Length: > 0 } bundled ? new[] { bundled } : [],
        .. OperatingSystem.IsWindows() ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeuib.ttf") } : [],
        Path.Combine(AppContext.BaseDirectory, "fonts", "DejaVuSans-Bold.ttf"),
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
    ];

    /// <summary>
    /// The family captions name. Windows finds <paramref name="wanted"/> among its own fonts; anywhere else the captions
    /// are drawn in the title font, which is the only one sure to be there, so they name that.
    /// </summary>
    public static string Family(string wanted) => OperatingSystem.IsWindows() ? wanted : FallbackFamily;

    /// <summary>The family name inside the font file bundled for systems with no fonts of their own.</summary>
    public const string FallbackFamily = "DejaVu Sans";
}