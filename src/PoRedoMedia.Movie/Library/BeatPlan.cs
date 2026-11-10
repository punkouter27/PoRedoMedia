namespace PoRedoMedia.Movie.Library;

/// <summary>
/// The shapes an episode can take. Every episode of a series follows one, so each is told the same way. The first
/// three tell a robot lesson, the next two a kids' video, and the last three a movie about anything. New structures go
/// on the end: saved episodes name theirs.
/// </summary>
public enum BeatStructure { Story, Tutorial, HookFirst, ToyReview, Highlights, Movie, HowTo, DayOut }

/// <summary>
/// Which series an episode belongs to: teaching the robot, a little boy playing (for young children), or General, a
/// movie about anything, which is what a new one is unless a preset is chosen. Each has its own brand kit, shapes,
/// prompts and numbering. An episode's shape says which it is, so nothing more is saved. New shows go on the end.
/// </summary>
public enum Show { Robot, Kids, General }

/// <summary>One section of an episode, shown on screen as a strap when it starts.</summary>
/// <param name="ForFootage">Real footage of the robot belongs here, more than pictures made for the episode.</param>
public sealed record Beat(string Name, bool ForFootage);

public static class BeatPlan
{
    /// <summary>The beats of a structure, in the order they are told.</summary>
    public static IReadOnlyList<Beat> Beats(BeatStructure structure) => structure switch
    {
        BeatStructure.Tutorial => [new("Concept", false), new("Setup", true), new("Demo", true), new("Recap", false)],
        BeatStructure.HookFirst => [new("Hook", true), new("Explain", false), new("Demo", true), new("Next time", false)],
        BeatStructure.ToyReview => [new("Hello", false), new("Unboxing", true), new("Play time", true), new("What he thinks", true), new("Bye", false)],
        BeatStructure.Highlights => [new("Hello", false), new("Best bits", true), new("Bye", false)],
        BeatStructure.Movie => [new("Opening", false), new("Highlights", true), new("Closing", false)],
        BeatStructure.HowTo => [new("What it is", false), new("Step by step", true), new("Result", true), new("Recap", false)],
        BeatStructure.DayOut => [new("Setting off", true), new("Best moments", true), new("Looking back", false)],
        _ => [new("Goal", false), new("Try", true), new("Fail", true), new("Fix", false), new("Result", true), new("Recap", false)],
    };

    /// <summary>The series an episode of this shape belongs to.</summary>
    public static Show ShowOf(BeatStructure structure) => structure switch
    {
        BeatStructure.ToyReview or BeatStructure.Highlights => Show.Kids,
        BeatStructure.Movie or BeatStructure.HowTo or BeatStructure.DayOut => Show.General,
        _ => Show.Robot,
    };

    /// <summary>The shapes a series can choose from, in the order they are offered.</summary>
    public static IReadOnlyList<BeatStructure> Structures(Show show) => show switch
    {
        Show.Kids => [BeatStructure.ToyReview, BeatStructure.Highlights],
        Show.General => [BeatStructure.Movie, BeatStructure.HowTo, BeatStructure.DayOut],
        _ => [BeatStructure.Story, BeatStructure.Tutorial, BeatStructure.HookFirst],
    };

    /// <summary>A shape's name as it is shown to choose from.</summary>
    public static string Label(BeatStructure structure) => structure switch
    {
        BeatStructure.Story => "Story",
        BeatStructure.Tutorial => "Tutorial",
        BeatStructure.HookFirst => "Hook first",
        BeatStructure.ToyReview => "Toy review",
        BeatStructure.Highlights => "Highlights",
        BeatStructure.Movie => "Movie",
        BeatStructure.HowTo => "How-to",
        _ => "Day out",
    };

    /// <summary>A series' name as it is shown.</summary>
    public static string Label(Show show) => show switch { Show.Kids => "Kids", Show.General => "General", _ => "Robot" };

    /// <summary>
    /// Holds a written script to its structure, whatever the model did: every clip in one of its beats and the beats
    /// in order, each footage file in exactly one clip, and no more than <paramref name="maxClips"/> clips unless the
    /// owner's footage needs them. A clip in a beat the structure does not have joins the beat before it.
    /// </summary>
    public static Episode Repair(Episode episode, IReadOnlyList<FootageNote> footage, int maxClips)
    {
        var beats = Beats(episode.Structure);
        var names = beats.Select(b => b.Name).ToList();
        var current = names[0];
        var clips = episode.Clips
            .Select(c => c with { Beat = current = c.Beat is { } beat && names.Contains(beat) ? beat : current })
            .OrderBy(c => names.IndexOf(c.Beat!)) // stable: clips keep their order within a beat
            .ToList();

        // A file used twice stays where footage belongs, else in its first clip; the other clip goes back to a title card.
        var keepers = clips.Where(c => c.Visual.UserVideoPath is not null)
            .GroupBy(c => c.Visual.UserVideoPath!, StringComparer.OrdinalIgnoreCase)
            .Select(same => same.OrderBy(c => beats[names.IndexOf(c.Beat!)].ForFootage ? 0 : 1).First().Id)
            .ToHashSet();
        // The one that stays takes the file's sound choice from the owner's note, whatever the clip said before.
        clips = [.. clips.Select(c => c.Visual.UserVideoPath is not { } path ? c
            : !keepers.Contains(c.Id) ? EpisodeEditor.Narratable(c with { Visual = new VisualSpec(VisualKind.TitleCard), OwnSound = false })
            : footage.FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)) is { } note ? WithFootage(c, note) : c)];

        // A file left out gets a clip of its own, at the end of the footage beat that has the least footage so far.
        foreach (var note in footage.Where(n => !clips.Any(c => string.Equals(c.Visual.UserVideoPath, n.Path, StringComparison.OrdinalIgnoreCase))))
        {
            var beat = beats.Where(b => b.ForFootage).MinBy(b => clips.Count(c => c.Beat == b.Name && c.Visual.UserVideoPath is not null)) ?? beats[^1];
            var at = clips.FindLastIndex(c => names.IndexOf(c.Beat!) <= names.IndexOf(beat.Name)) + 1;
            // Its words start from the owner's note, else what was seen in the film: never nothing, which the voice
            // service answers with no sound at all. They are fitted to the film's length when it is brought in.
            var said = Described(note) ?? "Look what happens here!";
            var blank = new Clip(Guid.NewGuid(), Title(note), Tier.B, new Dictionary<Tier, TierScript> { [Tier.B] = new(said, said, "none") },
                new VisualSpec(VisualKind.TitleCard), HostVisible: false)
            { Beat = beat.Name };
            clips.Insert(at, WithFootage(blank, note));
        }

        // Too long: clips without footage go from the end. The owner's footage is never what is cut.
        while (clips.Count > maxClips && clips.FindLastIndex(c => c.Visual.UserVideoPath is null) is >= 0 and var spare)
            clips.RemoveAt(spare);
        return episode with { Clips = clips };
    }

    /// <summary>
    /// Puts one of the owner's files in a clip. A file that keeps its own sound has nothing narrated over it, so the
    /// clip's words are cleared: the owner types what is said, if they want it captioned. The mascot stays out of real footage.
    /// </summary>
    public static Clip WithFootage(Clip clip, FootageNote note) => clip with
    {
        Visual = new VisualSpec(VisualKind.UserVideo, KindLocked: true, UserVideoPath: note.Path),
        OwnSound = note.OwnSound,
        HostVisible = false,
        FootageSeen = note.Seen?.Timeline ?? clip.FootageSeen,
        Scripts = note.OwnSound ? clip.Scripts.ToDictionary(s => s.Key, s => s.Value with { Dialogue = "" }) : clip.Scripts,
    };

    private static string Title(FootageNote note) =>
        Described(note) is { } words ? words.Length > 40 ? words[..40].TrimEnd() + "…" : words : "Your footage";

    /// <summary>What a film shows, in the owner's words or else in those of the model that watched it; null for neither.</summary>
    private static string? Described(FootageNote note) =>
        new[] { note.Note, note.Seen?.Note }.Select(t => t?.Trim()).FirstOrDefault(t => t is { Length: > 0 });
}

/// <summary>
/// How many clips an episode should have, besides any the owner's footage needs. Standard aims at the series' three
/// to five minutes.
/// </summary>
public sealed record EpisodeLength
{
    public static readonly EpisodeLength Standard = new(6, 10);

    public EpisodeLength(int minClips, int maxClips)
    {
        if (minClips < 1 || maxClips > 20 || minClips > maxClips)
            throw new ArgumentOutOfRangeException(nameof(minClips), "An episode has between 1 and 20 clips.");
        (MinClips, MaxClips) = (minClips, maxClips);
    }

    public int MinClips { get; }

    public int MaxClips { get; }

    /// <summary>The count in words, for prompts: "exactly 1 clip", "between 15 and 20 clips".</summary>
    public string InWords => MinClips == MaxClips ? $"exactly {MinClips} {(MinClips == 1 ? "clip" : "clips")}" : $"between {MinClips} and {MaxClips} clips";
}
