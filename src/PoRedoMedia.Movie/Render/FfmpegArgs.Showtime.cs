using System.Globalization;

namespace PoRedoMedia.Movie.Render;

// The cuts a movie made of films gets around its best moment: a cold open before the intro, and a slow replay.
public static partial class FfmpegArgs
{
    /// <summary>How much slower the replay runs than the moment did.</summary>
    public const double ReplaySlowdown = 2.0;

    /// <summary>
    /// A stretch of a film with its own sound and a label in the corner ("COMING UP", read from
    /// <paramref name="labelFile"/> in the working folder, beside <see cref="TitleFontFile"/>), at its own speed or slowed
    /// down by <paramref name="slowdown"/>. Slowed, the picture is pushed in a little and the sound drops an octave with
    /// the speed, the way a slow-motion replay sounds. A film with no sound gives silence.
    /// </summary>
    public static IReadOnlyList<string> Moment(string film, bool hasSound, TimeSpan start, TimeSpan length, string labelFile, double slowdown, string output)
    {
        var slow = slowdown > 1.001;
        var played = TimeSpan.FromSeconds(length.TotalSeconds * Math.Max(1, slowdown));
        var factor = Math.Max(1, slowdown).ToString("0.###", CultureInfo.InvariantCulture);
        var push = slow ? $"setpts={factor}*PTS,scale=trunc(iw*1.12/2)*2:-2,crop=trunc(iw/1.12/2)*2:trunc(ih/1.12/2)*2," : "";
        var label = $"drawtext=fontfile={TitleFontFile}:textfile={labelFile}:fontcolor=white:fontsize=h/14:box=1:boxcolor=black@0.55:boxborderw=14:x=w*0.04:y=h*0.06";
        var rate = (48000 / Math.Max(1, slowdown)).ToString("0", CultureInfo.InvariantCulture);
        var sound = hasSound
            ? $"[0:a]aformat=sample_rates=48000:channel_layouts=stereo,{(slow ? $"asetrate={rate},aresample=48000," : "")}apad,atrim=0:{Seconds(played)},afade=t=in:d=0.15,afade=t=out:st={Seconds(played - TimeSpan.FromSeconds(0.35))}:d=0.35[a]"
            : $"anullsrc=r=48000:cl=stereo,atrim=0:{Seconds(played)}[a]";
        return
        [
            "-ss", Seconds(start), "-t", Seconds(length), "-i", film,
            "-filter_complex", $"[0:v]{push}fps=30,setsar=1,{label}[v];{sound}",
            "-map", "[v]", "-map", "[a]", "-t", Seconds(played),
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-y", output,
        ];
    }

    /// <summary>
    /// The stretch of a film around its best moment, <paramref name="length"/> long, the moment a little after its
    /// middle so it builds up to it. Kept inside the film.
    /// </summary>
    public static TimeSpan MomentStart(double highlight, TimeSpan length, TimeSpan film)
    {
        var start = TimeSpan.FromSeconds(highlight) - length * 0.6;
        var latest = film - length;
        return latest <= TimeSpan.Zero ? TimeSpan.Zero : start < TimeSpan.Zero ? TimeSpan.Zero : start > latest ? latest : start;
    }
}
