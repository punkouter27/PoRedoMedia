using System.Globalization;
using System.Text;

namespace PoRedoMedia.Movie.Render;

/// <summary>
/// What a movie's download package says about it, all from the movie's own data (no AI is asked for anything): the
/// title, description, tags and hashtags its publish notes already hold, its series, and whether it is a kids' video.
/// </summary>
/// <param name="Category">A YouTube category id ("22" People &amp; Blogs, "28" Science &amp; Technology).</param>
public sealed record PackageInfo(
    string Title, string Description, IReadOnlyList<string> Tags, IReadOnlyList<string> Hashtags, string Series, bool MadeForKids, string Category);

/// <summary>
/// The text files of a movie's download package (youtube.txt, tiktok.txt, README.txt), held to each site's limits. Pure:
/// the zip itself is written by <see cref="EpisodeBuilder.PackageAsync"/>.
/// </summary>
public static class PublishPackage
{
    /// <summary>YouTube's limits: title and description in characters, tags as they count them (see <see cref="FitTags"/>).</summary>
    public const int YouTubeTitleMax = 100, YouTubeDescriptionMax = 5000, YouTubeTagsMax = 500;

    /// <summary>TikTok's caption limit, hashtags included.</summary>
    public const int TikTokCaptionMax = 2200;

    /// <summary>The file names inside the zip.</summary>
    public const string YouTubeVideo = "youtube-video.mp4", TikTokVideo = "tiktok-video-9x16.mp4", Thumbnail = "youtube-thumbnail.jpg",
        YouTubeText = "youtube.txt", TikTokText = "tiktok.txt", Readme = "README.txt";

    /// <summary>The TikTok frame and longest video the package makes for it.</summary>
    public const int TikTokWidth = 1080, TikTokHeight = 1920;

    public static readonly TimeSpan TikTokMaxLength = TimeSpan.FromMinutes(10);

    /// <summary>The thumbnail YouTube asks for: 1280 by 720, under 2 MB.</summary>
    public const int ThumbnailWidth = 1280, ThumbnailHeight = 720;

    public const long ThumbnailMaxBytes = 2 * 1024 * 1024;

    /// <summary>
    /// <paramref name="text"/> cut to at most <paramref name="max"/> characters: at a word break when one is near the
    /// end, with "…" to show it was cut, and never through the middle of a character made of two UTF-16 units (an emoji).
    /// </summary>
    public static string Cut(string? text, int max)
    {
        var whole = (text ?? "").Trim();
        if (whole.Length <= max) return whole;
        if (max <= 1) return max == 1 ? "…" : "";
        var end = max - 1;
        if (char.IsHighSurrogate(whole[end - 1])) end--;
        var space = whole.LastIndexOf(' ', end - 1, end);
        if (space > max * 3 / 4) end = space;
        return whole[..end].TrimEnd() + "…";
    }

    /// <summary>
    /// YouTube refuses &lt; and &gt; in a title and a description; they are taken out. Then cut to the limit.
    /// </summary>
    public static string YouTubeSafe(string? text, int max) => Cut((text ?? "").Replace("<", "", StringComparison.Ordinal).Replace(">", "", StringComparison.Ordinal), max);

    /// <summary>
    /// The tags that fit YouTube's 500: each counts its length, a comma between, and two more for the quotes YouTube
    /// adds around one with a space. Tags are kept in order, empty and repeated ones dropped, and one that does not
    /// fit is left out rather than cut.
    /// </summary>
    public static IReadOnlyList<string> FitTags(IEnumerable<string> tags, int max = YouTubeTagsMax)
    {
        var kept = new List<string>();
        var used = 0;
        foreach (var raw in tags)
        {
            var tag = raw.Replace(",", " ", StringComparison.Ordinal).Replace("<", "", StringComparison.Ordinal).Replace(">", "", StringComparison.Ordinal).Trim();
            if (tag.Length == 0 || kept.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            var cost = tag.Length + (tag.Contains(' ', StringComparison.Ordinal) ? 2 : 0) + (kept.Count > 0 ? 1 : 0);
            if (used + cost > max) continue;
            kept.Add(tag);
            used += cost;
        }
        return kept;
    }

    public static string CategoryName(string id) => id switch
    {
        "28" => "Science & Technology",
        "27" => "Education",
        "24" => "Entertainment",
        _ => "People & Blogs",
    };

    /// <summary>youtube.txt: each field as it goes in YouTube's upload form, within its limit.</summary>
    public static string YouTube(PackageInfo info)
    {
        var text = new StringBuilder()
            .AppendLine("TITLE")
            .AppendLine(YouTubeSafe(info.Title, YouTubeTitleMax))
            .AppendLine()
            .AppendLine("DESCRIPTION")
            .AppendLine(YouTubeSafe(info.Description, YouTubeDescriptionMax))
            .AppendLine()
            .AppendLine("TAGS")
            .AppendLine(string.Join(", ", FitTags(info.Tags)))
            .AppendLine()
            .AppendLine("CATEGORY (suggested)")
            .AppendLine(CultureInfo.InvariantCulture, $"{CategoryName(info.Category)} ({info.Category})")
            .AppendLine()
            .AppendLine("AUDIENCE");
        text.AppendLine(info.MadeForKids
            ? "Yes, it's made for kids. This is a Kids' series video: under Audience choose \"Yes, it's made for kids\" (YouTube requires it). Comments and personalised ads will be off."
            : "No, it's not made for kids.");
        return text.ToString();
    }

    /// <summary>tiktok.txt: the caption, title and description first, then hashtags, all within TikTok's 2,200.</summary>
    public static string TikTok(PackageInfo info)
    {
        var hashtags = string.Join(' ', info.Hashtags
            .Select(h => "#" + new string(h.TrimStart('#').Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray()))
            .Where(h => h.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        hashtags = Cut(hashtags, 300);
        var lastSpace = hashtags.EndsWith('…') ? hashtags.LastIndexOf(' ') : -1;
        if (lastSpace > 0) hashtags = hashtags[..lastSpace]; // a hashtag cut in half is no hashtag: drop the cut one
        var room = TikTokCaptionMax - (hashtags.Length > 0 ? hashtags.Length + 2 : 0);
        var words = Cut(string.Join("\n\n", new[] { info.Title.Trim(), FirstParagraph(info.Description) }.Where(p => p.Length > 0)), room);
        return "CAPTION\n" + (hashtags.Length > 0 ? $"{words}\n\n{hashtags}" : words) + "\n";
    }

    /// <summary>README.txt: which file goes where, and the steps on each site.</summary>
    public static string ReadmeText(PackageInfo info) => $"""
        {info.Title}
        {(info.Series.Length > 0 ? $"Series: {info.Series}\n" : "")}
        WHAT IS IN THIS PACKAGE
        {YouTubeVideo}  the movie for YouTube (MP4, H.264 video, AAC sound)
        {Thumbnail}  its YouTube thumbnail (1280 x 720 JPG, under 2 MB)
        {YouTubeText}  title, description, tags, category and audience for YouTube
        {TikTokVideo}  the movie upright for TikTok (1080 x 1920 MP4, up to 10 minutes)
        {TikTokText}  the TikTok caption with hashtags

        YOUTUBE
        1. Go to studio.youtube.com and click Create, then Upload videos.
        2. Choose {YouTubeVideo}.
        3. Copy the title, description and tags from {YouTubeText} into the matching boxes (tags are under Show more).
        4. Under Thumbnail, upload {Thumbnail}.
        5. Under Audience, answer as {YouTubeText} says{(info.MadeForKids ? " (this one is made for kids)" : "")}.
        6. Pick the category under Show more, choose who can watch it, and publish.

        TIKTOK
        1. Open TikTok (app or tiktok.com/upload) and start a new upload.
        2. Choose {TikTokVideo}.
        3. Paste the caption from {TikTokText}.
        4. Choose who can watch it and post.

        This package was made from the movie itself; nothing of the movie is kept on the server after you download it.
        """;

    private static string FirstParagraph(string description) =>
        (description ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
}
