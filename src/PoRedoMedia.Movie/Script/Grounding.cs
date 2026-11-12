namespace PoRedoMedia.Movie.Script;

/// <summary>
/// A passage a script is written from and checked against: its source's name, where in it, a web link to it, and the
/// words themselves. Nothing here fetches any; a script is handed whatever passages its host has.
/// </summary>
public sealed record GroundingSnippet(string Repo, string Path, string Url, string Text);
