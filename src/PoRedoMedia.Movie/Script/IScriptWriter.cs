
namespace PoRedoMedia.Movie.Script;

/// <summary>Something an editor would change about one clip's words, found by reading the whole script.</summary>
/// <param name="Clip">The clip's place in the running order, counting from 1.</param>
public sealed record ClipNote(int Clip, string Note);

/// <summary>Another picture for a clip whose own picture a picture model would not draw.</summary>
/// <param name="Diagram">A picture of the same idea that it will draw: a diagram, the tools, the place.</param>
/// <param name="SearchWords">A few plain words that would find a photograph of the place or the object.</param>
public sealed record SaferPicture(string Diagram, string SearchWords);

/// <summary>What a model saw happen in one of the owner's films, from a few of its frames.</summary>
/// <param name="Timeline">What happens, a line to each stretch, each line starting with its seconds.</param>
/// <param name="Note">One line on what the film shows, as the owner might label it.</param>
/// <param name="Highlight">The second of the most telling moment: a fall, a catch, a success.</param>
/// <param name="Stretches">The stretches worth keeping in a video, in time order; null when not asked for (watched before there were any).</param>
/// <param name="SubjectX">Where across the frame the main subject usually is: 0 the left edge, 0.5 the middle, 1 the right edge.</param>
public sealed record FootageSeen(string Timeline, string Note, double Highlight, IReadOnlyList<Stretch>? Stretches = null, double SubjectX = 0.5);

/// <summary>A stretch of a film worth keeping, as the model that watched it saw it.</summary>
/// <param name="Score">1 to 10: 10 is the best moment of the film.</param>
/// <param name="Shaky">The camera shakes hard in it.</param>
public sealed record Stretch(double Start, double End, int Score, bool Shaky);

/// <summary>One film, as the model that decides what kind of video the films make is told of it.</summary>
public sealed record FilmBrief(string Note, string Timeline, double Seconds, bool Talking, bool Portrait);

/// <summary>What kind of video a set of films makes, and how it is best told, as a model decided after reading about them.</summary>
/// <param name="Kind">A <see cref="Library.VideoKind"/>'s name.</param>
/// <param name="Pace">A <see cref="Library.Pace"/>'s name.</param>
/// <param name="Mood">The music bed's mood (see <see cref="Library.VideoKinds.Moods"/>).</param>
/// <param name="Narrator">A narrator style's id (warm, excited, deadpan, sports).</param>
/// <param name="Roast">How hard the roast goes, 0 (off) to 4.</param>
/// <param name="Line">One line saying what the video is, in a few words.</param>
public sealed record VideoPlan(string Kind, string Pace, string Mood, string Narrator, int Roast, string Line);

/// <summary>A clip a look at the finished clips found unusable.</summary>
/// <param name="Clip">Its place in the running order, counting from 1.</param>
/// <param name="Problem">What is wrong, in a few plain words.</param>
public sealed record ShotFault(int Clip, string Problem);

/// <summary>One frame taken from a film or a scene, and when in it.</summary>
public sealed record Frame(TimeSpan At, string Path);

/// <summary>What a look at a drawn scene found.</summary>
/// <param name="Problem">What to move or change, said as a designer would; empty when it is fine.</param>
public sealed record SceneVerdict(bool Fine, string Problem);

public interface IScriptWriter
{
    /// <summary>The longest topic sent to the model. A larger paste is cut here so it cannot run up a large bill.</summary>
    const int MaxTopicLength = 8000;

    /// <summary>
    /// The series of the open episode, for the jobs handed one of its clips or lines alone. A lesson and an episode
    /// say their own series by their shape, and a film is handed its own, and the jobs given one use that instead.
    /// </summary>
    Show Show { get; set; }

    /// <summary>
    /// Writes the lesson as an episode told in its beats, with each of the owner's footage files in one clip (the
    /// files are not copied here). Every clip is at depth B only; the other depths are written when first asked for.
    /// </summary>
    /// <param name="clipsWritten">Given the titles of the clips finished so far, as the script arrives.</param>
    Task<Episode> WriteEpisodeAsync(Lesson lesson, IReadOnlyList<GroundingSnippet> grounding, EpisodeLength length, CancellationToken ct, IProgress<IReadOnlyList<string>>? clipsWritten = null);

    /// <summary>Writes one clip at a depth it does not have yet, from the depth it is on now.</summary>
    Task<TierScript> WriteTierAsync(string topic, Clip clip, Tier tier, CancellationToken ct);

    /// <summary>True when the edit changes the core action, tool or physical subject, so the visual no longer fits.</summary>
    Task<bool> CoreChangedAsync(string oldDialogue, string newDialogue, CancellationToken ct);

    Task<string> RewriteToLengthAsync(string dialogue, int targetWords, CancellationToken ct);

    /// <summary>Writes an animated scene in code: a diagram, a chart or moving text, to run for the length asked.</summary>
    /// <param name="failed">The scene written last time, when its code did not run; it is then written again with <paramref name="problem"/> in hand.</param>
    Task<Scene> WriteSceneAsync(SceneRequest request, CancellationToken ct, Scene? failed = null, string? problem = null);

    /// <summary>Thinks of another picture for a clip whose picture the picture model declined to draw.</summary>
    Task<SaferPicture> RethinkPictureAsync(Clip clip, CancellationToken ct);

    /// <summary>
    /// Reads the whole script as an editor would and notes the clips worth changing; none when it reads well. With the
    /// passages it was written from, a claim none of them backs is noted too.
    /// </summary>
    Task<IReadOnlyList<ClipNote>> ReviewAsync(Episode episode, IReadOnlyList<GroundingSnippet> references, CancellationToken ct);

    /// <summary>The words drawn into a picture, as they appear; null when there are none, or nothing here can see pictures.</summary>
    Task<string?> CheckLetteringAsync(string picturePath, CancellationToken ct);

    /// <summary>Rewrites one clip's words to answer the editor's note on them, keeping what the note does not mention.</summary>
    Task<string> ReviseAsync(Clip clip, string note, CancellationToken ct);

    /// <summary>What happens in one of the owner's films, seen from a few of its frames; null when nothing here can see pictures.</summary>
    /// <param name="note">The owner's own line on it, when they wrote one.</param>
    /// <param name="show">The series the film is for, which says what to look for in it.</param>
    Task<FootageSeen?> WatchAsync(IReadOnlyList<Frame> frames, string note, Show show, CancellationToken ct);

    /// <summary>
    /// Decides what kind of video the owner's films make (kids, a trip, a party…) and how it is best told, from what
    /// was seen and heard in them. Null when nothing here can decide, and the usual choices are made.
    /// </summary>
    Task<VideoPlan?> PlanVideoAsync(IReadOnlyList<FilmBrief> films, CancellationToken ct) => Task.FromResult<VideoPlan?>(null);

    /// <summary>
    /// Looks at one frame from the middle of each finished clip, each labelled with its clip's number, for a clip
    /// that is plainly unusable: black, the lens covered, the floor or a pocket. None when nothing here can see.
    /// </summary>
    Task<IReadOnlyList<ShotFault>> CheckClipsAsync(IReadOnlyList<(int Clip, string Path)> frames, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ShotFault>>([]);

    /// <summary>Looks at a scene's frame once it has built, for labels cut off, overlapping or under the captions.</summary>
    Task<SceneVerdict> CheckSceneAsync(string framePath, CancellationToken ct);

    /// <summary>How a picture of the host differs from its locked design; null when it keeps it, or nothing can tell.</summary>
    Task<string?> CheckHostAsync(string picturePath, string sheetPath, CancellationToken ct);

    /// <summary>The words a lesson's passages would be found by in the official repositories, besides its own.</summary>
    Task<IReadOnlyList<string>> SearchTermsAsync(string topic, CancellationToken ct);

    /// <summary>A few shots to film for each beat of the lesson's structure where footage belongs, before filming it.</summary>
    Task<IReadOnlyList<Shot>> SuggestShotsAsync(Lesson lesson, CancellationToken ct);

    /// <summary>Titles, a description and tags to upload with the finished video, written from its script.</summary>
    Task<PublishNotes> WritePublishNotesAsync(Episode episode, CancellationToken ct);

    /// <summary>
    /// Three ten-second teasers for other video sites, cut from the finished episode, held to <see cref="Teasers"/>'
    /// rules. Where too few suggestions keep to them, teasers made by the rules alone fill in.
    /// </summary>
    /// <param name="lengths">How long each clip runs in the finished episode.</param>
    Task<IReadOnlyList<Teaser>> SuggestTeasersAsync(Episode episode, IReadOnlyDictionary<Guid, TimeSpan> lengths, CancellationToken ct);
}
