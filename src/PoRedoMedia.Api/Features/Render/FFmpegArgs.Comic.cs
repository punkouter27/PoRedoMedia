using System.Globalization;
using System.Text;

namespace PoRedoMedia.Api.Features.Render;

// The comic's command lines: each panel's sound (its spoken lines one after another, a meme sound under the start), each
// panel as a short moving clip, and the clips joined with a music bed and levelled to -16 LUFS.
internal static partial class FFmpegArgs
{
    /// <summary>The comic video's frame: square, like the panels.</summary>
    internal const int ComicFrame = 1080;

    /// <summary>The longest a meme sound plays under a panel.</summary>
    internal const double ComicSoundSeconds = 3;

    private const string Stereo = "aresample=44100,aformat=sample_fmts=fltp:channel_layouts=stereo";

    private static string S(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Silence of this length, standing in for a line when no voice is configured.</summary>
    internal static string ComicSilenceArgs(double seconds, string output) =>
        $"-y -f lavfi -i anullsrc=r=44100:cl=stereo -t {S(seconds)} -c:a pcm_s16le \"{output}\"";

    /// <summary>
    /// One panel's sound: the lines in order with <paramref name="gap"/> between them, starting after
    /// <paramref name="lead"/> and holding <paramref name="tail"/> after the last, with the meme sound (when there is one)
    /// from the panel's first moment, cut to <see cref="ComicSoundSeconds"/>.
    /// </summary>
    internal static string ComicPanelAudioArgs(IReadOnlyList<string> lines, string? sound, double lead, double gap, double tail, string output)
    {
        if (lines.Count == 0)
            throw new ArgumentException("A panel's sound needs at least one line, or a silence standing in for one.", nameof(lines));
        var sb = new StringBuilder("-y");
        foreach (var line in lines)
            sb.Append(CultureInfo.InvariantCulture, $" -i \"{line}\"");
        if (sound is not null)
            sb.Append(CultureInfo.InvariantCulture, $" -i \"{sound}\"");

        sb.Append(" -filter_complex \"");
        for (var i = 0; i < lines.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $"[{i}:a]{Stereo},apad=pad_dur={S(gap)}[l{i}];");
        for (var i = 0; i < lines.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $"[l{i}]");
        var leadMs = (int)Math.Round(lead * 1000);
        sb.Append(CultureInfo.InvariantCulture, $"concat=n={lines.Count}:v=0:a=1,adelay={leadMs}:all=1,apad=pad_dur={S(tail)}[speech];");
        if (sound is not null)
        {
            var fadeAt = ComicSoundSeconds - 0.5;
            sb.Append(CultureInfo.InvariantCulture,
                $"[{lines.Count}:a]{Stereo},atrim=0:{S(ComicSoundSeconds)},afade=t=out:st={S(fadeAt)}:d=0.5,volume=0.6[sfx];[speech][sfx]amix=inputs=2:duration=first:normalize=0[out]\"");
        }
        else
        {
            sb.Append("[speech]anull[out]\"");
        }
        sb.Append(CultureInfo.InvariantCulture, $" -map \"[out]\" -c:a pcm_s16le \"{output}\"");
        return sb.ToString();
    }

    /// <summary>
    /// One panel as a clip of <paramref name="seconds"/>: the lettered picture slowly pushing in, with a quick fade at
    /// each end, over the panel's sound.
    /// </summary>
    internal static string ComicPanelVideoArgs(string picture, string audio, double seconds, string output)
    {
        var frames = Math.Max(1, (int)Math.Round(seconds * 30));
        var fadeOut = Math.Max(0, seconds - 0.25);
        // zoompan is smoother from a picture twice the output size.
        var video = $"[0:v]scale={ComicFrame * 2}:{ComicFrame * 2},"
            + $"zoompan=z='1+0.05*on/{frames}':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d=1:s={ComicFrame}x{ComicFrame}:fps=30,"
            + $"fade=t=in:st=0:d=0.25,fade=t=out:st={S(fadeOut)}:d=0.25,format=yuv420p[v]";
        return $"-y -loop 1 -framerate 30 -t {S(seconds)} -i \"{picture}\" -i \"{audio}\" -filter_complex \"{video}\" "
            + $"-map \"[v]\" -map 1:a -t {S(seconds)} -c:v libx264 -preset veryfast -crf 22 -r 30 -c:a aac -b:a 160k -ar 44100 -ac 2 \"{output}\"";
    }

    /// <summary>The concat list that joins the panel clips, in order.</summary>
    internal static string ComicConcatList(IEnumerable<string> clips) =>
        string.Join("\n", clips.Select(c => "file '" + ConcatQuoted(c) + "'")) + "\n";

    /// <summary>A path inside the concat list's single quotes: forward slashes, and a quote closed, escaped and reopened.</summary>
    private static string ConcatQuoted(string path) =>
        path.Replace(Path.DirectorySeparatorChar == '\\' ? "\\" : "\0", "/", StringComparison.Ordinal)
            .Replace("'", "'\\''", StringComparison.Ordinal);

    /// <summary>
    /// The panel clips joined (the pictures copied, not encoded again), a music bed looped quietly under them and faded out
    /// over the last 1.5 s when there is one, and the whole sound levelled to -16 LUFS.
    /// </summary>
    internal static string ComicJoinArgs(string concatList, string? music, double totalSeconds, string output)
    {
        const string level = "loudnorm=I=-16:TP=-1.5:LRA=11";
        var head = $"-y -f concat -safe 0 -i \"{concatList}\"";
        var tail = $"-c:v copy -c:a aac -b:a 160k -ar 44100 -movflags +faststart \"{output}\"";
        if (music is null)
            return $"{head} -map 0:v -map 0:a -af {level} {tail}";
        var fadeAt = Math.Max(0, totalSeconds - 1.5);
        return $"{head} -stream_loop -1 -i \"{music}\" -filter_complex "
            + $"\"[1:a]{Stereo},volume=0.12,afade=t=out:st={S(fadeAt)}:d=1.5[m];[0:a][m]amix=inputs=2:duration=first:normalize=0,{level}[a]\" "
            + $"-map 0:v -map \"[a]\" {tail}";
    }
}
