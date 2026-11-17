using PoRedoMedia.Movie.Render;

namespace PoRedoMedia.UnitTests;

/// <summary>The download package's text, held to YouTube's and TikTok's limits (Render/PublishPackage).</summary>
public sealed class PublishPackageTests
{
    private static PackageInfo Info(string title = "Treehouse Day", string description = "We built a treehouse.", string[]? tags = null, string[]? hashtags = null, bool kids = false) =>
        new(title, description, tags ?? ["treehouse", "weekend"], hashtags ?? ["treehouse", "#diy"], "My Movies", kids, "22");

    [Fact]
    public void Text_is_cut_at_a_word_with_an_ellipsis_never_through_an_emoji_and_never_over_the_limit()
    {
        Assert.Equal("short", PublishPackage.Cut("  short  ", 10));
        var cut = PublishPackage.Cut("one two three four five six seven", 20);
        Assert.True(cut.Length <= 20);
        Assert.EndsWith("…", cut);
        Assert.DoesNotContain("fiv", cut); // stops at a word break
        var emoji = PublishPackage.Cut("abcdefgh\U0001F600xyz", 10); // the emoji would be split at 9
        Assert.False(char.IsHighSurrogate(emoji[^2]));
        Assert.True(emoji.Length <= 10);

        var title = new string('a', 300) + "<b>";
        var youTube = PublishPackage.YouTube(Info(title: title, description: new string('d', 6000)));
        var lines = youTube.Split('\n');
        Assert.True(lines[1].TrimEnd('\r').Length <= PublishPackage.YouTubeTitleMax);
        Assert.DoesNotContain('<', youTube);
        Assert.True(lines[4].TrimEnd('\r').Length <= PublishPackage.YouTubeDescriptionMax);
    }

    [Fact]
    public void Tags_fit_youtubes_500_counting_quotes_and_commas_and_none_is_cut_in_half()
    {
        var many = Enumerable.Range(0, 200).Select(i => $"tag number {i}").Append("tag number 1").Append("");
        var fitted = PublishPackage.FitTags(many);
        var cost = fitted.Sum(t => t.Length + (t.Contains(' ') ? 2 : 0)) + fitted.Count - 1;
        Assert.InRange(cost, 400, PublishPackage.YouTubeTagsMax);
        Assert.Equal(fitted.Count, fitted.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(fitted, t => Assert.StartsWith("tag number ", t));
    }

    [Fact]
    public void Tiktok_caption_keeps_whole_hashtags_within_2200_and_a_kids_video_says_so_for_youtube()
    {
        var caption = PublishPackage.TikTok(Info(description: new string('x', 5000), hashtags: [.. Enumerable.Range(0, 100).Select(i => $"tag{i}")]));
        var body = caption["CAPTION\n".Length..].TrimEnd('\n');
        Assert.True(body.Length <= PublishPackage.TikTokCaptionMax);
        Assert.Contains("#tag0 #tag1", body);
        Assert.Matches(@"#tag\d+$", body); // the last hashtag is whole
        Assert.Contains("#diy", PublishPackage.TikTok(Info()));

        Assert.Contains("made for kids", PublishPackage.YouTube(Info(kids: true)));
        Assert.Contains("No, it's not made for kids", PublishPackage.YouTube(Info()));
        Assert.Contains("People & Blogs (22)", PublishPackage.YouTube(Info()));
        Assert.Contains(PublishPackage.TikTokVideo, PublishPackage.ReadmeText(Info()));
    }
}
