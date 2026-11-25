using System.Globalization;

namespace PoRedoMedia.Movie.Render;

/// <summary>
/// Where the beats of a music track fall, so cuts can land on them. Found from how the track's loudness rises a few
/// milliseconds at a time (FFmpeg's sound statistics), with no model involved: the tempo is the spacing at which the
/// rises repeat best, and the first beat the offset at which they line up best.
/// </summary>
public static class BeatGrid
{
    /// <summary>Seconds of sound each loudness reading covers.</summary>
    public const double Hop = 0.005;

    /// <summary>The most of a track that is listened to: enough to hear a steady beat.</summary>
    public const double Listened = 45;

    private const double SlowestBpm = 70, FastestBpm = 170;

    /// <summary>The command that writes the track's loudness, every <see cref="Hop"/> seconds, to <paramref name="file"/> in the working folder.</summary>
    public static IReadOnlyList<string> Args(string track, string file) =>
    [
        "-t", Listened.ToString(CultureInfo.InvariantCulture), "-i", track, "-vn",
        "-af", $"aresample=8000,asetnsamples=n=40:p=0,astats=metadata=1:reset=1,ametadata=print:key=lavfi.astats.Overall.RMS_level:file={file}",
        "-f", "null", "-",
    ];

    /// <summary>The loudness in the file FFmpeg wrote, one reading to each <see cref="Hop"/>, as a level (not decibels). Silence is 0.</summary>
    public static List<double> Read(string text)
    {
        var levels = new List<double>();
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            const string Key = "lavfi.astats.Overall.RMS_level=";
            if (!line.StartsWith(Key, StringComparison.Ordinal)) continue;
            levels.Add(double.TryParse(line[Key.Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var db) && double.IsFinite(db)
                ? Math.Pow(10, db / 20) : 0);
        }
        return levels;
    }

    /// <summary>
    /// The beat's spacing and the time of its first beat, in seconds; null when no steady beat is heard (too short a
    /// track, or one with no beat to speak of).
    /// </summary>
    /// <param name="levels">Loudness, one reading every <paramref name="hop"/> seconds.</param>
    public static (double Period, double Phase)? Find(IReadOnlyList<double> levels, double hop = Hop)
    {
        if (levels.Count * hop < 6) return null;
        // Where the sound gets louder: a beat is a sudden rise.
        var rise = new double[levels.Count];
        for (var i = 1; i < levels.Count; i++) rise[i] = Math.Max(0, levels[i] - levels[i - 1]);
        var mean = rise.Average();
        if (mean <= 0) return null;
        for (var i = 0; i < rise.Length; i++) rise[i] = Math.Max(0, rise[i] - mean);

        // The spacing at which the rises repeat best, to the nearest reading.
        var (shortest, longest) = ((int)Math.Floor(60 / FastestBpm / hop), (int)Math.Ceiling(60 / SlowestBpm / hop));
        var scores = new Dictionary<int, double>();
        for (var lag = shortest; lag <= longest; lag++)
        {
            double sum = 0;
            for (var i = 0; i + lag < rise.Length; i++) sum += rise[i] * rise[i + lag];
            scores[lag] = sum / (rise.Length - lag);
        }
        var best = scores.MaxBy(s => s.Value);
        var usual = scores.Values.Average();
        if (best.Value <= 0 || best.Value < usual * 1.4) return null;

        // Then finer, together with where the beats start: a small error in the spacing adds up over a minute of cuts.
        var (period, phase, score) = (best.Key * hop, 0.0, double.MinValue);
        for (var p = (best.Key - 1) * hop; p <= (best.Key + 1) * hop; p += hop / 20)
            for (var offset = 0.0; offset < p; offset += hop)
            {
                double sum = 0;
                for (var t = offset; ; t += p)
                {
                    var at = (int)Math.Round(t / hop);
                    if (at >= rise.Length - 1) break;
                    sum += Math.Max(rise[at], Math.Max(at > 0 ? rise[at - 1] : 0, rise[at + 1]));
                }
                if (sum > score) (period, phase, score) = (p, offset, sum);
            }
        return (period, phase);
    }

    /// <summary>
    /// Every beat up to <paramref name="until"/> seconds, for a track <paramref name="length"/> long that is played
    /// again from its start when it ends (as the music bed is).
    /// </summary>
    public static IReadOnlyList<double> Beats((double Period, double Phase) beat, double length, double until)
    {
        var beats = new List<double>();
        if (beat.Period <= 0 || length <= beat.Phase) return beats;
        for (var loop = 0.0; loop < until; loop += length)
            for (var t = beat.Phase; t < length && loop + t <= until; t += beat.Period)
                beats.Add(loop + t);
        return beats;
    }

    /// <summary>
    /// How much to cut off the end of each clip so that each cut lands on a beat. Clips are laid end to end, each
    /// overlapping the next by <paramref name="overlap"/> (the sound's crossfade). Only the end of a clip is cut, never
    /// more than <paramref name="most"/> and never below <paramref name="shortest"/>; a cut with no beat that near is
    /// left where it is. The last clip is never cut: nothing follows it.
    /// </summary>
    /// <param name="lengths">Each clip's length, in seconds.</param>
    /// <param name="beats">Beat times, in seconds from the first clip's start, in order.</param>
    public static double[] Trims(IReadOnlyList<double> lengths, double overlap, IReadOnlyList<double> beats, double most, double shortest)
    {
        var trims = new double[lengths.Count];
        var start = 0.0;
        for (var k = 0; k < lengths.Count - 1; k++)
        {
            var cut = start + lengths[k] - overlap;
            var room = Math.Min(most, lengths[k] - shortest);
            var beat = beats.Where(b => b <= cut + 1e-9 && cut - b <= room).DefaultIfEmpty(double.NaN).Max();
            if (!double.IsNaN(beat))
            {
                trims[k] = cut - beat;
                cut = beat;
            }
            start = cut;
        }
        return trims;
    }
}
