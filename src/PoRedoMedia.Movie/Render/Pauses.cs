using System.Globalization;

namespace PoRedoMedia.Movie.Render;

/// <summary>
/// Turns for a narrator and a heckler. When a film is narrated for only part of its length (the roast has the rest),
/// the narration is spread across the film: a pause goes in after a sentence, again and again, so the narrator says a
/// line or two and then stops, and the roast answers in the pause. The words keep their times, moved on by the pauses.
/// </summary>
public static class Pauses
{
    /// <summary>A pause shorter than this holds no joke (a roast line runs two to four seconds).</summary>
    public const double MinPauseSeconds = 3.5;

    /// <summary>Spare time under this is not worth a pause: the narration plays as it is.</summary>
    public const double MinSpareSeconds = 2;

    /// <summary>
    /// Where to pause, in seconds of the narration as recorded, and for how long: after sentences spread through it,
    /// each pause the same length, with one more share left after the last word (the film runs on, and the roast can
    /// close it). Empty when there is too little spare time, or nowhere to break.
    /// </summary>
    public static IReadOnlyList<(double At, double Seconds)> Plan(IReadOnlyList<WordTiming> words, TimeSpan narration, TimeSpan film)
    {
        var spare = (film - narration).TotalSeconds;
        if (spare < MinSpareSeconds || words.Count < 2) return [];
        // Where a sentence ends (not the last word: after that the film simply runs on); else after a comma.
        var ends = Breaks(words, w => w.TrimEnd('"', '\'', ')', '”', '’').EndsWith('.') || w.EndsWith('!') || w.EndsWith('?') || w.EndsWith('…'));
        if (ends.Count == 0) ends = Breaks(words, w => w.EndsWith(',') || w.EndsWith(';') || w.EndsWith(':'));
        if (ends.Count == 0) return [];
        var count = Math.Clamp((int)(spare / MinPauseSeconds) - 1, 1, ends.Count);
        var each = spare / (count + 1);
        // Spread evenly through the sentences, so the turns come all through the film.
        return [.. Enumerable.Range(1, count)
            .Select(i => ends[Math.Clamp((int)Math.Round(i * (ends.Count + 1.0) / (count + 1)) - 1, 0, ends.Count - 1)])
            .Distinct().Order()
            .Select(at => (at, each))];
    }

    /// <summary>The moments between two words where a break could go: just after a word the test likes, before the next starts.</summary>
    private static List<double> Breaks(IReadOnlyList<WordTiming> words, Func<string, bool> breaksAfter) =>
        [.. Enumerable.Range(0, words.Count - 1)
            .Where(i => breaksAfter(words[i].Text.Trim()))
            .Select(i => ((words[i].Start + words[i].Duration).TotalSeconds + words[i + 1].Start.TotalSeconds) / 2)];

    /// <summary>The words moved on by the pauses before them.</summary>
    public static IReadOnlyList<WordTiming> Shift(IReadOnlyList<WordTiming> words, IReadOnlyList<(double At, double Seconds)> pauses) =>
        [.. words.Select(w => w with { Start = w.Start + TimeSpan.FromSeconds(pauses.Where(p => p.At <= w.Start.TotalSeconds).Sum(p => p.Seconds)) })];

    /// <summary>
    /// The narration with silence put in at each pause and padded to <paramref name="film"/> long, in the narration's
    /// form (48 kHz stereo WAV).
    /// </summary>
    public static IReadOnlyList<string> Args(string narration, IReadOnlyList<(double At, double Seconds)> pauses, TimeSpan film, string output)
    {
        static string S(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);
        var graph = new List<string> { $"[0:a]aformat=sample_rates=48000:channel_layouts=stereo,asplit={pauses.Count + 1}{string.Concat(Enumerable.Range(0, pauses.Count + 1).Select(i => $"[p{i}]"))}" };
        var parts = new List<string>();
        var from = 0.0;
        for (var i = 0; i <= pauses.Count; i++)
        {
            var to = i < pauses.Count ? pauses[i].At : (double?)null;
            graph.Add($"[p{i}]atrim=start={S(from)}{(to is { } t ? $":end={S(t)}" : "")},asetpts=PTS-STARTPTS[t{i}]");
            parts.Add($"[t{i}]");
            if (to is { } at)
            {
                graph.Add($"aevalsrc=0:c=stereo:s=48000:d={S(pauses[i].Seconds)}[g{i}]");
                parts.Add($"[g{i}]");
                from = at;
            }
        }
        graph.Add($"{string.Concat(parts)}concat=n={parts.Count}:v=0:a=1,apad[out]");
        return ["-i", narration, "-filter_complex", string.Join(';', graph), "-map", "[out]", "-t", S(film.TotalSeconds), "-c:a", "pcm_s16le", "-y", output];
    }
}
