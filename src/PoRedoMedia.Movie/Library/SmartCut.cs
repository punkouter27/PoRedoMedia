using System.Globalization;
using System.Text.RegularExpressions;

namespace PoRedoMedia.Movie.Library;

/// <summary>What kind of video a set of films makes. It decides the series, the shape, the pace and the music.</summary>
public enum VideoKind { Everyday, Kids, Pets, Robot, Trip, Party, Sport, HowTo, Gaming }

/// <summary>How quickly a movie cuts: short shots for sport and parties, long ones for trips and calm scenes.</summary>
public enum Pace { Fast, Medium, Calm }

/// <summary>What each kind of video is told as, how fast and to what music.</summary>
public static class VideoKinds
{
    /// <summary>The music bed's moods, as the planning model may name them.</summary>
    public static readonly IReadOnlyList<string> Moods = ["upbeat", "energetic", "calm", "playful"];

    public static (Show Show, BeatStructure Structure) SeriesOf(VideoKind kind) => kind switch
    {
        VideoKind.Kids => (Show.Kids, BeatStructure.Highlights),
        VideoKind.Robot => (Show.Robot, BeatStructure.Story),
        VideoKind.Trip => (Show.General, BeatStructure.DayOut),
        VideoKind.HowTo => (Show.General, BeatStructure.HowTo),
        _ => (Show.General, BeatStructure.Movie),
    };

    /// <summary>The kind a shape chosen by hand stands for.</summary>
    public static VideoKind Of(BeatStructure structure) => structure switch
    {
        BeatStructure.DayOut => VideoKind.Trip,
        BeatStructure.HowTo => VideoKind.HowTo,
        BeatStructure.Highlights or BeatStructure.ToyReview => VideoKind.Kids,
        BeatStructure.Story or BeatStructure.Tutorial or BeatStructure.HookFirst => VideoKind.Robot,
        _ => VideoKind.Everyday,
    };

    public static Pace PaceOf(VideoKind kind) => kind switch
    {
        VideoKind.Sport or VideoKind.Party or VideoKind.Gaming => Pace.Fast,
        VideoKind.Trip or VideoKind.HowTo => Pace.Calm,
        _ => Pace.Medium,
    };

    public static string MoodOf(VideoKind kind) => kind switch
    {
        VideoKind.Sport or VideoKind.Party or VideoKind.Gaming => "energetic",
        VideoKind.Trip or VideoKind.HowTo => "calm",
        VideoKind.Kids or VideoKind.Pets => "playful",
        _ => "upbeat",
    };

    /// <summary>
    /// Whether the films are best shown in the order they were filmed: a trip, a how-to or a lesson happens in order.
    /// The others open on their best moment and build to a high.
    /// </summary>
    public static bool InTimeOrder(VideoKind kind) => kind is VideoKind.Trip or VideoKind.HowTo or VideoKind.Robot or VideoKind.Everyday;

    public static string Label(VideoKind kind) => kind switch
    {
        VideoKind.HowTo => "How-to",
        VideoKind.Everyday => "Everyday",
        _ => kind.ToString(),
    };

    public static VideoKind? Parse(string? name) =>
        Enum.TryParse<VideoKind>(name?.Replace("-", "", StringComparison.Ordinal), ignoreCase: true, out var kind) && Enum.IsDefined(kind) ? kind : null;

    public static Pace? ParsePace(string? name) =>
        Enum.TryParse<Pace>(name, ignoreCase: true, out var pace) && Enum.IsDefined(pace) ? pace : null;
}

/// <summary>A stretch of one film that goes into the movie, in seconds into the film.</summary>
/// <param name="Score">1 to 10, from the model that watched it; 5 when it was not watched.</param>
public sealed record Part(double Start, double End, int Score, bool Shaky)
{
    public double Length => End - Start;
}

/// <summary>
/// Which stretches of a film go into the movie: the ones worth keeping, each held to the pace's shot lengths, and never
/// ending in the middle of a word. No model and no FFmpeg: the model's stretches come in, the parts come out.
/// </summary>
public static partial class SmartCut
{
    /// <summary>The shortest part kept: shorter is a flash, not a shot.</summary>
    public const double MinPart = 2;

    /// <summary>A stretch scored this or better is kept; a film with none keeps its best.</summary>
    public const int KeepScore = 5;

    /// <summary>How long a shot runs at each pace, in seconds: no shorter than the first, no longer than the second.</summary>
    public static (double Min, double Max) ShotLength(Pace pace) => pace switch
    {
        Pace.Fast => (2.5, 6),
        Pace.Calm => (5, 18),
        _ => (3.5, 10),
    };

    /// <param name="stretches">What the model that watched the film thought worth keeping; null or empty when it was not watched.</param>
    /// <param name="film">How long the film runs, in seconds.</param>
    /// <param name="highlight">The second of its best moment; a stretch too long for the pace is cut around it.</param>
    /// <param name="heard">What is said in it, so no part starts or ends in the middle of a word; null when not heard.</param>
    /// <param name="whole">Keep the whole film as one part (the owner chose whole films).</param>
    public static IReadOnlyList<Part> Choose(IReadOnlyList<Stretch>? stretches, double film, Pace pace, double? highlight, IReadOnlyList<WordTiming>? heard, bool whole)
    {
        var usable = (stretches ?? [])
            .Select(s => s with { Start = Math.Clamp(s.Start, 0, film), End = Math.Clamp(s.End, 0, film), Score = Math.Clamp(s.Score, 1, 10) })
            .Where(s => s.End - s.Start >= 0.3)
            .OrderBy(s => s.Start)
            .ToList();
        if (whole || usable.Count == 0)
        {
            var score = usable.Count == 0 ? KeepScore : usable.Max(s => s.Score);
            // Shaky when most of what was watched shakes.
            var shaky = usable.Count > 0 && usable.Where(s => s.Shaky).Sum(s => s.End - s.Start) > film / 2;
            return [new Part(0, film, score, shaky)];
        }

        var kept = usable.Where(s => s.Score >= KeepScore).ToList();
        if (kept.Count == 0) kept = [usable.MaxBy(s => s.Score)!];
        var (min, max) = ShotLength(pace);
        var parts = new List<Part>();
        foreach (var s in kept)
        {
            var (start, end) = (s.Start, s.End);
            if (end - start > max)
            {
                // Too long a shot for the pace: the stretch around its best moment, which comes a little after the middle.
                var centre = highlight is { } h && h >= start && h <= end ? h : (start + end) / 2;
                start = Math.Clamp(centre - max * 0.6, s.Start, s.End - max);
                end = start + max;
            }
            else if (end - start < min)
            {
                // Too short: grown on both sides, as far as the film allows.
                var grow = (min - (end - start)) / 2;
                (start, end) = (Math.Max(0, start - grow), Math.Min(film, end + grow));
                if (end - start < min) (start, end) = start == 0 ? (0, Math.Min(film, min)) : (Math.Max(0, film - min), film);
            }
            parts.Add(new Part(start, end, s.Score, s.Shaky));
        }

        // Parts that touch or overlap become one shot while it stays a sensible length; otherwise the later one gives way.
        var merged = new List<Part>();
        foreach (var part in parts.OrderBy(p => p.Start))
        {
            if (merged.Count > 0 && merged[^1] is var last && part.Start <= last.End + 0.75)
            {
                if (Math.Max(last.End, part.End) - last.Start <= max * 1.3)
                {
                    merged[^1] = new Part(last.Start, Math.Max(last.End, part.End), Math.Max(last.Score, part.Score), last.Shaky || part.Shaky);
                    continue;
                }
                if (part.End <= last.End) continue;
                merged.Add(part with { Start = last.End });
                continue;
            }
            merged.Add(part);
        }

        List<Part> chosen = [.. merged
            .Select(p => p with { Start = SnapStart(p.Start, heard), End = SnapEnd(p.End, heard, film) })
            .Where(p => p.Length >= MinPart)];
        // A film too short to cut a part from is kept whole, never left out for that alone.
        return chosen.Count > 0 ? chosen : [new Part(0, film, kept.Max(s => s.Score), kept.Any(s => s.Shaky))];
    }

    /// <summary>A start in the middle of a word moves back to where the word starts.</summary>
    public static double SnapStart(double start, IReadOnlyList<WordTiming>? heard)
    {
        foreach (var word in heard ?? [])
        {
            var (from, to) = (word.Start.TotalSeconds, (word.Start + word.Duration).TotalSeconds);
            if (from < start && start < to) return Math.Max(0, from - 0.05);
        }
        return start;
    }

    /// <summary>An end in the middle of a word moves on to where it ends, or back to before it when that is far.</summary>
    public static double SnapEnd(double end, IReadOnlyList<WordTiming>? heard, double film)
    {
        foreach (var word in heard ?? [])
        {
            var (from, to) = (word.Start.TotalSeconds, (word.Start + word.Duration).TotalSeconds);
            if (from < end && end < to) return to - end <= 0.8 ? Math.Min(film, to + 0.1) : from;
        }
        return end;
    }

    /// <summary>
    /// Keeps the best parts that fit <paramref name="budget"/> seconds and <paramref name="most"/> parts, in the order
    /// given. The best part is always kept, even when it alone runs over.
    /// </summary>
    public static List<T> Fit<T>(IReadOnlyList<T> parts, Func<T, double> length, Func<T, int> score, double budget, int most)
    {
        var chosen = new HashSet<int>();
        var total = 0.0;
        foreach (var i in Enumerable.Range(0, parts.Count).OrderByDescending(i => score(parts[i])).ThenBy(i => i))
        {
            if (chosen.Count >= most) break;
            var add = length(parts[i]);
            if (chosen.Count > 0 && total + add > budget) continue;
            chosen.Add(i);
            total += add;
        }
        return [.. Enumerable.Range(0, parts.Count).Where(chosen.Contains).Select(i => parts[i])];
    }

    /// <summary>
    /// Whether people talk through a part, so it keeps its own sound instead of being narrated over: about a word a
    /// second or more of what was heard, and a few words at least.
    /// </summary>
    public static bool Talking(IReadOnlyList<WordTiming>? heard, double start, double end)
    {
        if (heard is null || end <= start) return false;
        var words = heard.Count(w => w.Start.TotalSeconds >= start && w.Start.TotalSeconds < end);
        return words >= 3 && words / (end - start) >= 0.9;
    }

    /// <summary>The words heard in a part, timed from its start.</summary>
    public static IReadOnlyList<WordTiming>? Slice(IReadOnlyList<WordTiming>? heard, double start, double end) =>
        heard is null ? null
            : [.. heard.Where(w => w.Start.TotalSeconds >= start && w.Start.TotalSeconds < end)
                .Select(w => w with { Start = w.Start - TimeSpan.FromSeconds(start) })];

    /// <summary>
    /// Holds a written script to the order the footage was arranged in, whatever order the model wrote it in: each
    /// footage clip moves to its film's place, and every place keeps its beat, so the beats still run in order. The
    /// replay comes from the best-scored part, leaving out the opening shot when there are three or more (it is already
    /// seen first).
    /// </summary>
    public static Episode InShowOrder(Episode episode, IReadOnlyList<FootageNote> footage)
    {
        int Rank(Clip clip) => clip.Visual.UserVideoPath is { } path
            ? Enumerable.Range(0, footage.Count).FirstOrDefault(i => string.Equals(footage[i].Path, path, StringComparison.OrdinalIgnoreCase), -1) : -1;
        var clips = episode.Clips.ToList();
        var places = Enumerable.Range(0, clips.Count).Where(i => Rank(clips[i]) >= 0).ToList();
        var sorted = places.Select(i => clips[i]).OrderBy(Rank).ToList();
        for (var k = 0; k < places.Count; k++)
            clips[places[k]] = sorted[k] with { Beat = episode.Clips[places[k]].Beat };

        var candidates = Enumerable.Range(0, clips.Count).Where(i => Rank(clips[i]) >= 0 && footage[Rank(clips[i])].Score > 0).ToList();
        if (candidates.Count >= 3) candidates.RemoveAt(0);
        if (candidates.Count > 0)
        {
            var payoff = candidates.OrderByDescending(i => footage[Rank(clips[i])].Score).ThenByDescending(i => i).First();
            clips = [.. clips.Select((c, i) => c with { Payoff = i == payoff })];
        }
        return episode with { Clips = clips };
    }

    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*(?:-|–|to)\s*(\d+(?:\.\d+)?)\s*s?\s*:\s*(.*)$")]
    private static partial Regex TimelineLine();

    /// <summary>
    /// The lines of a film's timeline ("0-4 s: …") that fall in a part, timed from the part's start, so the narration
    /// is written for what the part shows. A timeline whose lines cannot be read is kept whole, saying which seconds the part is.
    /// </summary>
    public static string Slice(string timeline, double start, double end)
    {
        var lines = timeline.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var read = lines.Select(l => TimelineLine().Match(l)).Where(m => m.Success)
            .Select(m => (From: double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), To: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), What: m.Groups[3].Value))
            .ToList();
        if (read.Count == 0)
            return string.Create(CultureInfo.InvariantCulture, $"(This clip is seconds {start:0}-{end:0} of the film.) {timeline}");
        var length = end - start;
        var inside = read.Where(l => l.To > start && l.From < end).ToList();
        if (inside.Count == 0) inside = [read.MinBy(l => Math.Abs((l.From + l.To) / 2 - (start + end) / 2))];
        return string.Join('\n', inside.Select(l => string.Create(CultureInfo.InvariantCulture,
            $"{Math.Clamp(l.From - start, 0, length):0}-{Math.Clamp(l.To - start, 0, length):0} s: {l.What}")));
    }
}

/// <summary>What a film's file says about it: its picture's size as shown, and when and where it was filmed when the phone kept that.</summary>
public sealed record FilmFacts(int Width, int Height, DateTimeOffset? Recorded, (double Lat, double Lon)? Place)
{
    public bool Portrait => Height > Width;
}

/// <summary>
/// The order the films are shown in, and which belong to one scene. Films filmed close together in time and place are
/// a scene; a trip or a how-to runs in the order it was filmed, and the rest open on their best and build to a high.
/// </summary>
public static class Arrange
{
    /// <summary>Films further apart than this in time start a new scene.</summary>
    public static readonly TimeSpan SceneGap = TimeSpan.FromMinutes(20);

    /// <summary>Films further apart than this, in kilometres, start a new scene.</summary>
    public const double SceneDistance = 0.5;

    /// <summary>
    /// The scene of each film (in the order given), numbered from 0 in the order they happened, and the films in time
    /// order. When fewer than half of the films say when they were filmed, each film is a scene of its own and the
    /// order is the order given.
    /// </summary>
    public static (int[] Scene, int[] TimeOrder) Scenes(IReadOnlyList<FilmFacts> films)
    {
        var n = films.Count;
        if (n == 0) return ([], []);
        if (films.Count(f => f.Recorded is not null) * 2 < n)
            return ([.. Enumerable.Range(0, n)], [.. Enumerable.Range(0, n)]);
        // A film that does not say when it was filmed goes beside the one given before it, else after it.
        var times = new DateTimeOffset[n];
        for (var i = 0; i < n; i++)
            times[i] = films[i].Recorded
                ?? Enumerable.Range(0, i).Reverse().Select(j => films[j].Recorded).FirstOrDefault(t => t is not null)
                ?? Enumerable.Range(i + 1, n - i - 1).Select(j => films[j].Recorded).First(t => t is not null)!.Value;
        var order = Enumerable.Range(0, n).OrderBy(i => times[i]).ThenBy(i => i).ToArray();
        var scene = new int[n];
        var current = 0;
        for (var k = 0; k < n; k++)
        {
            if (k > 0)
            {
                var (a, b) = (films[order[k - 1]], films[order[k]]);
                var apart = times[order[k]] - times[order[k - 1]] > SceneGap
                    || (a.Place is { } p && b.Place is { } q && Kilometres(p, q) > SceneDistance);
                if (apart) current++;
            }
            scene[order[k]] = current;
        }
        return (scene, order);
    }

    /// <summary>
    /// The order to show the films in. In time order, scenes and films run as filmed. Otherwise the strongest scene
    /// opens the movie and the rest build up to the next strongest at the end; a movie that is all one scene does the
    /// same with its films. Films without a known time keep the order given.
    /// </summary>
    /// <param name="scores">Each film's best part's score.</param>
    public static int[] Order(IReadOnlyList<FilmFacts> films, IReadOnlyList<int> scores, bool timeOrder)
    {
        var (scene, byTime) = Scenes(films);
        if (timeOrder) return byTime;
        var scenes = byTime.GroupBy(i => scene[i]).Select(g => g.ToList()).ToList(); // in time order, films in time order within
        if (scenes.Count == 1) return [.. Story(scenes[0], i => scores[i])];
        var ordered = Story(Enumerable.Range(0, scenes.Count).ToList(), s => scenes[s].Max(i => scores[i]));
        return [.. ordered.SelectMany(s => scenes[s])];
    }

    /// <summary>The strongest first; the rest weakest to strongest, so it builds to a high. Ties keep their order.</summary>
    private static List<int> Story(List<int> items, Func<int, int> score)
    {
        if (items.Count < 3) return [.. items.OrderByDescending(score)];
        var best = items.OrderByDescending(score).First();
        return [best, .. items.Where(i => i != best).OrderBy(score)];
    }

    /// <summary>The distance between two places on the Earth, in kilometres.</summary>
    public static double Kilometres((double Lat, double Lon) a, (double Lat, double Lon) b)
    {
        const double Radius = 6371;
        double Rad(double degrees) => degrees * Math.PI / 180;
        var (dLat, dLon) = (Rad(b.Lat - a.Lat), Rad(b.Lon - a.Lon));
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(a.Lat)) * Math.Cos(Rad(b.Lat)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * Radius * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}
