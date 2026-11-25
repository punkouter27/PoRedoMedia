using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoRedoMedia.Movie.Render;

/// <summary>The shape of a finished movie: wide (16:9) for a full video, tall (9:16) for a Short.</summary>
public enum Shape { Wide, Tall }

// Mixed footage made to sit together: every part cut from its film at the movie's shape, steadied when it shakes and
// brought to the others' brightness, and what is measured first to decide that.
public static partial class FfmpegArgs
{
    /// <summary>The size every part is made at: Meme-ify works at no more than 1280 across, so nothing is lost.</summary>
    public static (int Width, int Height) PartSize(Shape shape) => shape == Shape.Tall ? (720, 1280) : (1280, 720);

    /// <summary>
    /// How a film is fitted to the movie's shape, steadied and brightened, as the start of a filter graph reading
    /// [0:v] and ending in [v]; null when the film needs none of it (its shape is the movie's, it is steady and its
    /// brightness is like the others').
    /// </summary>
    /// <param name="width">The film's width as it is shown (turned upright, as a phone films).</param>
    /// <param name="subjectX">Where across the frame the main subject is (0 to 1): a crop is kept on it.</param>
    /// <param name="brightness">How much to lift or lower the picture, -1 to 1; 0 leaves it.</param>
    public static string? Framing(int width, int height, Shape shape, double subjectX, bool shaky, double brightness, bool always = false)
    {
        var (w, h) = PartSize(shape);
        var (source, target) = ((double)width / Math.Max(1, height), (double)w / h);
        var sameShape = Math.Abs(source / target - 1) < 0.03;
        if (sameShape && !shaky && Math.Abs(brightness) < 0.005 && !always) return null;

        var after = (shaky ? ",deshake=rx=32:ry=32" : "")
            + (Math.Abs(brightness) >= 0.005 ? string.Create(CultureInfo.InvariantCulture, $",eq=brightness={brightness:0.###}") : "")
            + ",fps=30,setsar=1,format=yuv420p[v]";
        // Much taller than the frame (a phone held upright, in a wide movie): cropping would lose the subject's head
        // or feet, so it is shown whole over a blurred, darkened copy of itself filling the frame.
        if (source < target / 1.3)
            return $"[0:v]split=2[bgsrc][fgsrc];[bgsrc]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},gblur=sigma=24,eq=brightness=-0.06[bg];" +
                $"[fgsrc]scale={w}:{h}:force_original_aspect_ratio=decrease[fg];[bg][fg]overlay=(W-w)/2:(H-h)/2" + after;
        // Otherwise it fills the frame, cut down to the movie's shape around the subject.
        var x = Math.Clamp(subjectX, 0, 1).ToString("0.###", CultureInfo.InvariantCulture);
        return $"[0:v]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}:x='max(0,min(iw-ow,{x}*iw-ow/2))':y=(ih-oh)/2" + after;
    }

    /// <summary>
    /// One part of a film as a film of its own, <paramref name="length"/> long from <paramref name="start"/>, through
    /// <paramref name="graph"/> (see <see cref="Framing"/>), with its own sound when it has any.
    /// </summary>
    public static IReadOnlyList<string> Part(string film, double start, double length, string graph, bool hasSound, string output) =>
    [
        "-ss", start.ToString("0.###", CultureInfo.InvariantCulture), "-t", length.ToString("0.###", CultureInfo.InvariantCulture), "-i", film,
        "-filter_complex", graph, "-map", "[v]",
        .. hasSound ? new[] { "-map", "0:a:0", "-c:a", "aac", "-b:a", "160k" } : ["-an"],
        "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-y", output,
    ];

    /// <summary>The command that writes how bright a film is, once a second (0 black to 255 white), to <paramref name="file"/> in the working folder.</summary>
    public static IReadOnlyList<string> Luma(string film, string file) =>
        ["-i", film, "-an", "-vf", $"fps=1,scale=160:-2,signalstats,metadata=print:key=lavfi.signalstats.YAVG:file={file}", "-f", "null", "-"];

    /// <summary>The command that marks each nearly black frame of a film, five a second, in <paramref name="file"/> in the working folder.</summary>
    public static IReadOnlyList<string> Black(string film, string file) =>
        ["-i", film, "-an", "-vf", $"fps=5,scale=160:-2,blackframe=amount=95:threshold=24,metadata=print:key=lavfi.blackframe.pblack:file={file}", "-f", "null", "-"];

    /// <summary>The values of one key in a file FFmpeg's metadata filter wrote, in order.</summary>
    public static List<double> MetadataValues(string text, string key)
    {
        var values = new List<double>();
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (line.StartsWith(key + "=", StringComparison.Ordinal) && double.TryParse(line[(key.Length + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                values.Add(value);
        return values;
    }

    /// <summary>
    /// How much to lift (or lower) a film's brightness to come near the others': nothing when it is close already, and
    /// never so much that a dark scene looks washed out.
    /// </summary>
    /// <param name="average">The film's average brightness, 0 to 255.</param>
    /// <param name="target">The movie's usual brightness, 0 to 255.</param>
    public static double Brightness(double average, double target)
    {
        var gap = target - average;
        return Math.Abs(gap) < 14 ? 0 : Math.Clamp(gap / 255 * 0.8, -0.1, 0.14);
    }

    /// <summary>The ffprobe command that lists a film's picture size, how it is turned, and the tags its camera wrote.</summary>
    public static IReadOnlyList<string> Facts(string film) =>
        ["-v", "error", "-print_format", "json", "-show_entries", "format_tags:stream=codec_type,width,height:stream_tags=rotate:stream_side_data=rotation", film];

    // A phone writes its time zone as -0700; read as -07:00.
    [GeneratedRegex(@"([+-]\d\d)(\d\d)$")]
    private static partial Regex ZoneWithColon();

    [GeneratedRegex(@"([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)")]
    private static partial Regex Iso6709();

    /// <summary>
    /// What ffprobe said about a film (<see cref="Facts"/>): its size as shown (a phone's upright film is stored on
    /// its side and turned when played), and when and where it was filmed if the camera wrote it down.
    /// </summary>
    public static FilmFacts ReadFacts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var (width, height, rotation) = (0, 0, 0.0);
        if (root.TryGetProperty("streams", out var streams))
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var type) || type.GetString() != "video") continue;
                width = stream.TryGetProperty("width", out var wv) ? wv.GetInt32() : 0;
                height = stream.TryGetProperty("height", out var hv) ? hv.GetInt32() : 0;
                if (stream.TryGetProperty("side_data_list", out var side))
                    foreach (var data in side.EnumerateArray())
                        if (data.TryGetProperty("rotation", out var r) && r.TryGetDouble(out var degrees)) rotation = degrees;
                if (rotation == 0 && stream.TryGetProperty("tags", out var st) && st.TryGetProperty("rotate", out var rotate)
                    && double.TryParse(rotate.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var tagged)) rotation = tagged;
                break;
            }
        if (Math.Abs(Math.Abs(rotation) % 180 - 90) < 1) (width, height) = (height, width);

        DateTimeOffset? recorded = null;
        (double, double)? place = null;
        if (root.TryGetProperty("format", out var format) && format.TryGetProperty("tags", out var tags))
        {
            string? Tag(string name) => tags.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
            // The phone's own date keeps its time zone; creation_time is often the moment the file was written instead.
            foreach (var name in new[] { "com.apple.quicktime.creationdate", "creation_time", "date" })
                if (Tag(name) is { } text && DateTimeOffset.TryParse(ZoneWithColon().Replace(text, "$1:$2"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) && when.Year > 1990)
                {
                    recorded = when;
                    break;
                }
            if ((Tag("com.apple.quicktime.location.ISO6709") ?? Tag("location")) is { } spot && Iso6709().Match(spot) is { Success: true } m
                && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                && double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
                && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180 && (lat, lon) != (0, 0))
                place = (lat, lon);
        }
        return new FilmFacts(width, height, recorded, place);
    }
}
