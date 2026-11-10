namespace PoRedoMedia.Movie.Render;

// The cover picture YouTube shows for the episode: made here from the episode's own frames, so it costs nothing.
public static partial class FfmpegArgs
{
    /// <summary>
    /// A 1280 by 720 thumbnail: a frame filled to the edges and given a little more punch, with the episode's title
    /// large in the accent colour, outlined in black, over a dark band along the foot, and the channel's label in the
    /// corner. From a film, the liveliest frame of a few seconds from <paramref name="from"/> (FFmpeg's thumbnail
    /// filter); from nothing, the dark of the bookends.
    /// </summary>
    /// <param name="titleFile">Text files in the working folder: the words are drawn, never run.</param>
    public static IReadOnlyList<string> Thumbnail(string? input, bool video, TimeSpan from, string titleFile, string? labelFile, string accent, string output)
    {
        string[] source = input is null ? ["-f", "lavfi", "-i", "color=c=0x101828:s=1280x720"] : video ? ["-ss", Seconds(from), "-t", "6", "-i", input] : ["-i", input];
        var colour = "0x" + accent.TrimStart('#');
        var graph = (video ? "thumbnail=150," : "") +
            "scale=1280:720:force_original_aspect_ratio=increase,crop=1280:720,setsar=1,eq=contrast=1.08:saturation=1.3," +
            "drawbox=x=0:y=ih*0.5:w=iw:h=ih*0.5:color=black@0.5:t=fill," +
            $"drawbox=x=0:y=ih-12:w=iw:h=12:color={colour}:t=fill," +
            $"drawtext=textfile={titleFile}:expansion=none:fontfile={TitleFontFile}:fontcolor={colour}:fontsize=88:line_spacing=4:borderw=7:bordercolor=black:x=56:y=h-text_h-56" +
            (labelFile is null ? "" : $",drawtext=textfile={labelFile}:expansion=none:fontfile={TitleFontFile}:fontcolor=white:fontsize=34:x=40:y=36:box=1:boxcolor=black@0.55:boxborderw=14");
        return [.. source, "-vf", graph, "-frames:v", "1", "-q:v", "2", "-y", output];
    }
}

public sealed partial class EpisodeBuilder
{
    /// <summary>The thumbnail chosen for upload, by this name in the episode's export folder.</summary>
    public const string ThumbnailName = "thumbnail.jpg";

    /// <summary>The master video rendered last for an episode, in its export folder; null when none has been.</summary>
    public static string? LatestMaster(string folder) =>
        new DirectoryInfo(Path.Combine(folder, "export")) is { Exists: true } export
            ? export.EnumerateFiles("*.mp4").Where(f => f.Name != "preview.mp4").MaxBy(f => f.LastWriteTimeUtc)?.FullName
            : null;

    /// <summary>
    /// Up to three thumbnails to choose from, each from a different source: the owner's films first, as the real
    /// robot sells the episode best, then the pictures made for its other clips; one plain one when there is neither.
    /// Made in a scratch folder (swept at the next launch); the one chosen is copied to the export folder.
    /// </summary>
    /// <returns>The pictures' paths.</returns>
    public async Task<IReadOnlyList<string>> ThumbnailsAsync(Episode episode, Brand? brand, CancellationToken ct)
    {
        var films = episode.Clips.Select(c => c.Visual.UserVideoPath).OfType<string>().Where(File.Exists).Select(p => (Path: (string?)Path.GetFullPath(p), Video: true));
        var pictures = episode.Clips.Where(c => c.Visual.UserVideoPath is null).Select(c => c.Picture()).OfType<string>().Select(p => (Path: (string?)Path.GetFullPath(p), Video: false));
        var sources = films.Concat(pictures).DistinctBy(s => s.Path).Take(3).ToList();
        if (sources.Count == 0) sources.Add((null, false));

        var work = Files.NewScratchFolder("thumbnails");
        CopyTitleFont(work);
        await File.WriteAllTextAsync(Path.Combine(work, "title.txt"), TitleLines(episode.Title), ct);
        var label = Bookends(brand) is { } named ? named.ChannelName + (episode.Number > 0 ? $" · #{episode.Number}" : "") : null;
        if (label is not null)
            await File.WriteAllTextAsync(Path.Combine(work, "label.txt"), label, ct);
        var accent = brand?.Kit.Captions.AccentColor ?? new CaptionStyle().AccentColor;
        var made = new List<string>();
        foreach (var (path, video) in sources)
        {
            // From just before the middle of a film: past any fumbling with the camera at its start.
            var from = video ? TimeSpan.FromTicks(Math.Max(0, (await ffmpeg.ProbeDurationAsync(path!, ct) / 2 - TimeSpan.FromSeconds(3)).Ticks)) : TimeSpan.Zero;
            var output = Path.Combine(work, $"thumbnail-{made.Count + 1}.jpg");
            await ffmpeg.RunAsync(FfmpegArgs.Thumbnail(path, video, from, "title.txt", label is null ? null : "label.txt", accent, output), work, null, null, ct);
            made.Add(output);
        }
        return made;
    }

    // A title is broken into lines of about 22 characters and cut at three; a longer one ends in "…" so the cut shows.
    private static string TitleLines(string title)
    {
        var lines = new List<string> { "" };
        foreach (var word in Durations.SplitWords(title))
            if (lines[^1].Length > 0 && lines[^1].Length + word.Length >= 22) lines.Add(word);
            else lines[^1] = (lines[^1] + " " + word).Trim();
        if (lines.Count > 3)
            lines[2] += "…";
        return string.Join('\n', lines.Take(3));
    }
}
