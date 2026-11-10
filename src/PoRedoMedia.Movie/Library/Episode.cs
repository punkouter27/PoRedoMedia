namespace PoRedoMedia.Movie.Library;

public enum Tier { A, B, C }

// New kinds go on the end: saved episodes name their kinds, and tests count on the order of the first five.
public enum VisualKind { Still, MultiPanel, AiVideo, TitleCard, UserVideo, Animation, Chart, KineticText, Stock, Parallax }

/// <summary>The art direction every generated picture in an episode shares.</summary>
public enum Look { Comic, Photoreal, FlatVector, Cinematic }

public sealed record TierScript(string Dialogue, string VisualPrompt, string Pose);

/// <param name="KindLocked">The user picked this kind by hand; mix re-rolls leave it alone.</param>
/// <param name="Stale">Dialogue changed the core action, so the generated media no longer matches.</param>
/// <param name="Take">Which attempt the media is: 0 the first, one more each time another is asked for.</param>
/// <param name="EarlierTakes">The media of the attempts not in use, oldest first, kept so any of them can be gone back to free.</param>
public sealed record VisualSpec(
    VisualKind Kind,
    bool KindLocked = false,
    bool Stale = false,
    string? UserVideoPath = null,
    IReadOnlyList<string>? MediaPaths = null,
    int Take = 0,
    IReadOnlyList<IReadOnlyList<string>>? EarlierTakes = null);

public sealed record Clip(
    Guid Id,
    string Title,
    Tier ActiveTier,
    IReadOnlyDictionary<Tier, TierScript> Scripts,
    VisualSpec Visual,
    bool HostVisible,
    double NarrationRate = 1.0)
{
    public TierScript Active => Scripts[ActiveTier];

    /// <summary>
    /// A picture that stands for the clip: its first generated picture still on disk, or for footage (generated or
    /// the user's own) the frame taken from it. Null when there is neither.
    /// </summary>
    public string? Picture() => PictureOf((Visual.MediaPaths ?? []).Append(Visual.UserVideoPath).OfType<string>());

    /// <summary>The picture that stands for a set of media files: the first that is a picture, or a video's saved frame.</summary>
    public static string? PictureOf(IEnumerable<string> media) =>
        media.Select(path => IsPicture(path) ? path : PosterFor(path)).FirstOrDefault(File.Exists);

    /// <summary>Every media file the clip holds on to: what it shows now, its footage, and its earlier takes.</summary>
    public IEnumerable<string> AllMedia() =>
        (Visual.MediaPaths ?? []).Append(Visual.UserVideoPath).OfType<string>().Concat((Visual.EarlierTakes ?? []).SelectMany(take => take));

    private static bool IsPicture(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg";

    /// <summary>Where the frame taken from a video to stand for it is kept: beside the video.</summary>
    public static string PosterFor(string videoPath) => Path.ChangeExtension(videoPath, ".poster.png");

    /// <summary>Where what is said in the owner's film, with when each word is said, is kept: beside the film.</summary>
    public static string HeardFor(string videoPath) => Path.ChangeExtension(videoPath, ".heard.json");

    /// <summary>Speaking pace multiplier; moves off 1.0 only to fit narration to the user's footage.</summary>
    public double NarrationRate { get; init; } = NarrationRate;

    /// <summary>The name of the beat the clip belongs to; null for clips written before episodes had beats.</summary>
    public string? Beat { get; init; }

    /// <summary>The user's footage plays with its own sound instead of being narrated over.</summary>
    public bool OwnSound { get; init; }

    /// <summary>The user's footage is shown as the robot's own camera might see it: depth colours, traced edges and a readout.</summary>
    public bool RobotView { get; init; }

    /// <summary>What a model saw happen in the owner's film, a line to each stretch; null when it was not watched.</summary>
    public string? FootageSeen { get; init; }

    /// <summary>The pictures it shows are quick drafts, to be drawn again at full quality once the episode is right.</summary>
    public bool Draft { get; init; }

    /// <summary>The user switched Meme-ify off for this clip: its film plays in the movie as it is. Clips are meme-ified unless this is set.</summary>
    public bool NoMemes { get; init; }
}

/// <summary>
/// Share of the plain clips (stills, panel sequences and title cards) given each of those three kinds; they must add
/// up to 100. The other kinds are never dealt: they are chosen clip by clip, by the script or by hand.
/// </summary>
public sealed record MixPercentages(int Still, int MultiPanel, int TitleCard)
{
    public static readonly MixPercentages Default = new(60, 25, 15);
}

// Fields added after the first save are optional parameters, not properties with a value of their own: the JSON code
// written at build time sets every init property, and one missing from an older file would lose its default.
public sealed record Episode(
    string Title,
    string Topic,
    IReadOnlyList<Clip> Clips,
    int MixSeed,
    CaptionStyle? Captions = null,
    MixPercentages? Mix = null,
    IReadOnlyList<Shot>? Shots = null)
{
    public CaptionStyle Captions { get; init; } = Captions ?? new();

    /// <summary>Where the episode comes in the series, from 1; 0 for episodes saved before they were numbered.</summary>
    public int Number { get; init; }

    /// <summary>The shape the episode is told in. Episodes saved before there was a choice are stories.</summary>
    public BeatStructure Structure { get; init; }

    public MixPercentages Mix { get; init; } = Mix ?? MixPercentages.Default;

    /// <summary>Episodes saved before there was a choice have no value here and are comic-book episodes.</summary>
    public Look Look { get; init; }

    /// <summary>A music track played quietly under the narration, inside the episode folder; null for none.</summary>
    public string? MusicPath { get; init; }

    /// <summary>
    /// How the main thing in the pictures looks (the robot, the toy), in a sentence, so every picture draws it alike
    /// from one reference picture; null for episodes written before there was one, which are drawn without it.
    /// </summary>
    public string? Subject { get; init; }

    /// <summary>The shots planned for filming the lesson, as ticked off; empty when none were asked for.</summary>
    public IReadOnlyList<Shot> Shots { get; init; } = Shots ?? [];

    /// <summary>The first picture any clip has on disk, used as the episode's thumbnail; null when there is none.</summary>
    public string? Cover() => Clips.Select(clip => clip.Picture()).FirstOrDefault(path => path is not null);
}
