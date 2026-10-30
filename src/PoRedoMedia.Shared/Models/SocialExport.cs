namespace PoRedoMedia.Shared.Models;

/// <summary>
/// The frames a video can be re-rendered to for posting (<c>/api/media/{id}/export/social?aspect=</c>).
/// The aspect travels as written here, width x height.
/// </summary>
public static class SocialExport
{
    public const string Vertical = "9x16";
    public const string Square = "1x1";
    public const string Wide = "16x9";

    public static readonly string[] Aspects = [Vertical, Square, Wide];

    public static string Label(string aspect) => aspect switch
    {
        Vertical => "9:16 (Reels, TikTok, Shorts)",
        Square => "1:1 (feed post)",
        Wide => "16:9 (YouTube)",
        _ => aspect,
    };
}
