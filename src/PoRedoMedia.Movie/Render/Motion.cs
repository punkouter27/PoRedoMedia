using System.Globalization;

namespace PoRedoMedia.Movie.Render;

/// <summary>
/// The moments a film's picture changes most suddenly: a fall, a crash, a jump, a toy flung. Found from how much each
/// frame differs from the one before (FFmpeg's scene score), a tenth of a second apart, so a meme sound or an effect
/// can land on the exact frame of the action instead of somewhere near it. No model involved.
/// </summary>
public static class Motion
{
    /// <summary>Frames a second the picture is measured at.</summary>
    public const int Rate = 10;

    /// <summary>Peaks closer than this collapse into the strongest.</summary>
    private const double MinSpacingSeconds = 1.2;

    /// <summary>A change smaller than this is the camera's own drift, not an action.</summary>
    private const double MinScore = 0.06;

    /// <summary>The command that writes each frame's scene score to <paramref name="scoresFile"/> in the working folder. Small and quick.</summary>
    public static IReadOnlyList<string> Args(string film, string scoresFile) =>
    [
        "-i", film, "-an",
        // Timed from the film's first frame: a phone's film can start its clock later than 0.
        "-vf", $"setpts=PTS-STARTPTS,fps={Rate},scale=160:-2,select=gte(scene\\,0),metadata=print:key=lavfi.scene_score:file={scoresFile}",
        "-f", "null", "-",
    ];

    /// <summary>The scores in the file FFmpeg wrote: "frame:… pts_time:1.2" lines, each followed by "lavfi.scene_score=0.123".</summary>
    public static List<(double At, double Score)> Read(string text)
    {
        var scores = new List<(double, double)>();
        double? at = null;
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var time = line.IndexOf("pts_time:", StringComparison.Ordinal);
            if (time >= 0)
            {
                var value = line[(time + 9)..].Split(' ')[0];
                at = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : null;
            }
            else if (at is { } when && line.StartsWith("lavfi.scene_score=", StringComparison.Ordinal)
                && double.TryParse(line[18..], NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
            {
                scores.Add((when, score));
                at = null;
            }
        }
        return scores;
    }

    /// <summary>
    /// The strongest sudden changes, in time order: each well above what is usual for this film, a local peak, and
    /// <see cref="MinSpacingSeconds"/> from any stronger one. At most one for every two seconds of film.
    /// </summary>
    public static IReadOnlyList<double> Peaks(IReadOnlyList<(double At, double Score)> scores)
    {
        if (scores.Count < 3) return [];
        var sorted = scores.Select(s => s.Score).Order().ToList();
        var usual = sorted[sorted.Count / 2];
        var floor = Math.Max(MinScore, usual * 3);
        var candidates = Enumerable.Range(1, scores.Count - 2)
            .Where(i => scores[i].Score >= floor && scores[i].Score >= scores[i - 1].Score && scores[i].Score >= scores[i + 1].Score)
            .OrderByDescending(i => scores[i].Score);
        var kept = new List<double>();
        var most = Math.Max(1, (int)((scores[^1].At - scores[0].At) / 2));
        foreach (var i in candidates)
        {
            if (kept.Count >= most) break;
            if (kept.All(k => Math.Abs(k - scores[i].At) >= MinSpacingSeconds)) kept.Add(scores[i].At);
        }
        return [.. kept.Order()];
    }
}
