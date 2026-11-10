using System.Globalization;

namespace PoRedoMedia.Movie.Render;

// The upright teasers: their moments cut, joined and hooked.
public static partial class FfmpegArgs
{
    /// <summary>
    /// An upright teaser: its moments cut from the finished clips and their sound and joined, the 16:9 picture centred
    /// over a blurred, enlarged copy of itself (or cropped to fill the frame), the hook across the top, captions, and
    /// the channel's name in a bar at the foot. Its sound is levelled like the master's.
    /// </summary>
    /// <param name="hookFiles">The hook's lines (see <see cref="HookLines"/>), a text file each in the working folder, as for a title card: the words are drawn, never run.</param>
    /// <param name="fill">Crop the picture to fill the frame instead of showing it whole over a blurred copy.</param>
    /// <param name="size">A smaller upright frame for a quick draft to watch; the full 1080 by 1920 when null.</param>
    public static IReadOnlyList<string> TeaserVideo(IReadOnlyList<TeaserCut> cuts, IReadOnlyList<string> hookFiles, string? channelFile, string? captionsFile, string output,
        bool fill = false, ExportPreset? size = null)
    {
        var preset = size ?? ExportPreset.Upright;
        var (w, h, fps) = preset;
        int Px(int full) => full * h / ExportPreset.Upright.Height; // sizes are set for the full frame and shrink with a draft's
        var length = TimeSpan.FromTicks(cuts.Sum(c => c.Length.Ticks));
        var graph = new List<string> { $"{string.Concat(cuts.Select((_, i) => $"[{2 * i}:v][{2 * i + 1}:a]"))}concat=n={cuts.Count}:v=1:a=1[cv][ca]" };
        string framed;
        if (fill)
        {
            framed = $"[cv]fps={fps},scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}";
        }
        else
        {
            graph.Add($"[cv]fps={fps},split[back][front]");
            graph.Add($"[back]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},boxblur={Px(24)}:2[bg]");
            graph.Add($"[front]scale={w}:-2[fg]");
            framed = "[bg][fg]overlay=(W-w)/2:(H-h)/2";
        }
        graph.Add(framed +
            (captionsFile is null ? "" : $",ass={captionsFile}:fontsdir=.") +
            string.Concat(hookFiles.Select((file, i) => $",drawtext=textfile={file}:expansion=none:fontfile={TitleFontFile}:fontcolor=white:fontsize=w/15:x=(w-text_w)/2:" +
                $"{Drop(i, $"h*0.09+{i * Px(150)}", "h*0.12")}:box=1:boxcolor=black@0.5:boxborderw={Px(28)}")) +
            (channelFile is null ? "" : $",drawbox=x=0:y=ih-{Px(150)}:w=iw:h={Px(150)}:color=black@0.55:t=fill,drawtext=textfile={channelFile}:expansion=none:fontfile={TitleFontFile}:fontcolor=white:fontsize={Px(48)}:x=(w-text_w)/2:y=h-{Px(102)}") +
            $",fade=t=in:st=0:d={Seconds(Fade)},fade=t=out:st={Seconds(length - Fade)}:d={Seconds(Fade)},format=yuv420p[v]");
        graph.Add($"[ca]loudnorm=I={TargetLufs.ToString(CultureInfo.InvariantCulture)}:TP=-1.5:LRA=11,aresample=48000[aout]");
        return
        [
            .. cuts.SelectMany(c => new[] { "-ss", Seconds(c.Start), "-t", Seconds(c.Length), "-i", c.Picture, "-ss", Seconds(c.Start), "-t", Seconds(c.Length), "-i", c.Sound }),
            "-filter_complex", string.Join(';', graph), "-map", "[v]", "-map", "[aout]",
            "-t", Seconds(length), .. Encode(preset), "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-y", output,
        ];
    }

    /// <summary>
    /// Where line <paramref name="line"/> of a teaser's hook is drawn: each falls from <paramref name="height"/> above its
    /// place at <paramref name="rest"/>, a moment after the line before, and bounces to rest as a dropped thing does,
    /// each bounce lower and quicker than the last.
    /// </summary>
    private static string Drop(int line, string rest, string height)
    {
        var at = FormattableString.Invariant($"(t-{0.15 + 0.12 * line:0.00})");
        // The bounces are a fading wave folded upwards: |cos| draws the arcs, and the falling e makes each one lower.
        return $"y='{rest}-{height}*exp(-5*max(0,{at}))*abs(cos(9*max(0,{at})))':alpha='min(1,max(0,{at}/0.08+1))'";
    }

    /// <summary>A hook broken into lines of at most four words, so it fits across the narrow frame.</summary>
    public static string HookLines(string hook) =>
        string.Join('\n', Durations.SplitWords(hook).Chunk(4).Select(line => string.Join(' ', line)));
}
