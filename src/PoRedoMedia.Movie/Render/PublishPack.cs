using System.Globalization;
using System.Text;

namespace PoRedoMedia.Movie.Render;

/// <summary>The text files written beside a finished video so it is ready to upload.</summary>
public static class PublishPack
{
    /// <summary>YouTube shows no chapter shorter than this, and no list with one.</summary>
    public static readonly TimeSpan MinChapter = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The chapter list for a video description, "0:00 Title" a line, from where each stretch of the timeline starts;
    /// stretches running on under the same title are one chapter. YouTube shows chapters only when the first is at
    /// 0:00, there are at least three, and none is under ten seconds, so the shortest chapter joins the one before it
    /// (the first joins the one after) until none is too short. Fewer than three left gives no list at all: empty.
    /// </summary>
    /// <param name="end">Where the video ends, which is where the last chapter does.</param>
    public static string Chapters(IEnumerable<(TimeSpan Start, string Title)> marks, TimeSpan end)
    {
        var chapters = new List<(TimeSpan Start, string Title)>();
        foreach (var mark in marks)
            if (chapters.Count == 0 || chapters[^1].Title != mark.Title) chapters.Add(mark);
        TimeSpan Length(int i) => (i + 1 < chapters.Count ? chapters[i + 1].Start : end) - (i == 0 ? TimeSpan.Zero : chapters[i].Start);
        while (chapters.Count > 1 && Enumerable.Range(0, chapters.Count).MinBy(Length) is var shortest && Length(shortest) < MinChapter)
            chapters.RemoveAt(shortest);
        if (chapters.Count < 3) return "";
        chapters[0] = (TimeSpan.Zero, chapters[0].Title); // what came before the first chapter left is now in it
        return string.Concat(chapters.Select(c => $"{(int)c.Start.TotalMinutes}:{c.Start.Seconds:00} {c.Title}\n"));
    }

    /// <summary>Title choices, description, tags and hashtags, laid out to copy into an upload form.</summary>
    public static string Description(PublishNotes notes) =>
        $"TITLES\n{string.Join('\n', notes.Titles)}\n\nDESCRIPTION\n{notes.Description}\n\n{string.Join(' ', notes.Hashtags)}\n\nTAGS\n{string.Join(", ", notes.Tags)}\n";

    /// <summary>The narration as a subtitle file, one plain line at a time whatever the burned-in caption style is.</summary>
    public static string Srt(IEnumerable<CaptionSegment> segments)
    {
        var srt = new StringBuilder();
        var number = 1;
        foreach (var cue in AssCaptions.Cues(segments, new CaptionStyle(CaptionPreset.CleanSubtitle)))
            srt.Append(CultureInfo.InvariantCulture, $"{number++}\n{Time(cue.Start)} --> {Time(cue.End)}\n{string.Join(' ', cue.Words)}\n\n");
        return srt.ToString();

        static string Time(TimeSpan t) => t.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
    }
}

/// <summary>What to paste into the upload form beside the finished video.</summary>
public sealed record PublishNotes(IReadOnlyList<string> Titles, string Description, IReadOnlyList<string> Tags, IReadOnlyList<string> Hashtags)
{
    /// <summary>
    /// Starts every title with the series' name and the episode's number ("Teaching the R1 #7: …"), so the uploads
    /// read as one series. A title that already starts with the name is left as it is, and so are all of them when
    /// the series has no name yet.
    /// </summary>
    public PublishNotes InSeries(string channel, int number)
    {
        if (channel.Trim() is not { Length: > 0 } name) return this;
        var prefix = number > 0 ? $"{name} #{number}: " : $"{name}: ";
        return this with { Titles = [.. Titles.Select(t => t.StartsWith(name, StringComparison.OrdinalIgnoreCase) ? t : prefix + t)] };
    }
}
