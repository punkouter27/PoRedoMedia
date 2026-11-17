namespace PoRedoMedia.Movie.Library;

/// <summary>
/// The series' accent colour: the intro and outro, the section straps, the thumbnail and the film finish take it.
/// Kept under its old name (it also held the burned-in captions' preset, size and outline until 2026-11-17, when
/// captions of what is said were removed) so saved movies and kits load as they are; their old caption fields are
/// skipped when read.
/// </summary>
/// <param name="AccentColor">#RRGGBB.</param>
public sealed record CaptionStyle(string AccentColor = "#FFD400");
