namespace PoRedoMedia.Movie.Library;

/// <summary>
/// Where the intro's words and mascot sit. Side: the words on the left, the mascot on the right. Centre: the mascot
/// above, the words centred under it. Lower: the mascot on the right, the words on a dark band across the lower third.
/// </summary>
public enum IntroLayout { Side, Centre, Lower }

/// <summary>
/// What every episode of the series shares: the channel's name, its logo, sounds and mascot for the intro and outro,
/// and the look and shape new episodes start in. Set up once and kept beside the app's settings, never in an episode.
/// </summary>
/// <remarks>
/// The files are the kit's own copies, named by what they are for ("logo.png"), so moving or deleting the file the
/// user picked changes nothing. Only a bare file name is kept, and only a file in the kit's folder is ever used.
/// </remarks>
public sealed record SeriesKit(string ChannelName = "", string Tagline = "", CaptionStyle? Captions = null, int OutroSeconds = 15)
{
    public const string FileName = "series.json";
    public const int MinOutroSeconds = 5;
    public const int MaxOutroSeconds = 20; // YouTube end-screen cards can be shown over the last 5 to 20 seconds

    public string ChannelName { get; init; } = ChannelName;

    public string Tagline { get; init; } = Tagline;

    public string? Logo { get; init; }

    /// <summary>The few seconds of music the intro plays.</summary>
    public string? Sting { get; init; }

    /// <summary>The music played quietly under every new episode.</summary>
    public string? Music { get; init; }

    public string? Mascot { get; init; }

    public Look Look { get; init; }

    /// <summary>The shape every episode follows, so each lesson is told the same way.</summary>
    public BeatStructure Structure { get; init; }

    public CaptionStyle Captions { get; init; } = Captions ?? new();

    public int OutroSeconds { get; init; } = OutroSeconds;

    /// <summary>How the intro is laid out. The outro keeps one layout, as YouTube's end-screen cards need its right half.</summary>
    public IntroLayout Layout { get; init; }

    /// <summary>A film finish (fine moving grain and darker corners) over every clip, the intro and the outro alike. Off unless chosen.</summary>
    public bool Grain { get; init; }

    public TimeSpan Outro => TimeSpan.FromSeconds(OutroSeconds);

    /// <summary>
    /// What a new series starts with, before anything is set up: the channel this app is made for. Every picture and sound
    /// left unset has a default of the app's own too (a robot's head for the logo, a robot for the mascot, a waking robot
    /// for the sting), made when it is drawn.
    /// </summary>
    public static SeriesKit Starter { get; } = new() { ChannelName = "Teaching the R1", Tagline = "One skill at a time" };

    /// <summary>
    /// What the kids' series starts with: a cheerful name to change, a toy review for its shape and a brighter colour.
    /// </summary>
    /// <remarks>
    /// ponytail: its logo and mascot left unset are a teddy of the app's own (<c>Emblem</c>), but its sting is still the
    /// robot series' one (<c>Synth.Sting</c>), until the owner picks their own on the Series step. Give <c>Synth</c> a
    /// brighter toy-box sting, chosen by show in <c>EpisodeBuilder.Bookends</c>, to lift it.
    /// </remarks>
    public static SeriesKit KidsStarter { get; } = new()
    {
        ChannelName = "Playtime Adventures",
        Tagline = "Toys, games and big smiles",
        Structure = BeatStructure.ToyReview,
        Captions = new CaptionStyle() with { AccentColor = "#FF5CA8" },
    };

    /// <summary>What a series of movies about anything starts with: a plain name to change, the simplest shape and a calm blue.</summary>
    public static SeriesKit GeneralStarter { get; } = new()
    {
        ChannelName = "My Movies",
        Structure = BeatStructure.Movie,
        Captions = new CaptionStyle() with { AccentColor = "#4DA3FF" },
    };

    public static SeriesKit StarterFor(Show show) => show switch { Show.Kids => KidsStarter, Show.General => GeneralStarter, _ => Starter };

    /// <summary>
    /// The kit of <paramref name="show"/> in <paramref name="folder"/>, made safe to use; that show's starter kit when
    /// there is none or it is unreadable. A shape that belongs to the other show goes back to the show's first.
    /// </summary>
    public static SeriesKit Load(string folder, Show show = Show.Robot)
    {
        var kit = JsonFile.Load(Path.Combine(folder, FileName), StarterFor(show), ProjectStore.JsonOptions);
        return kit with
        {
            ChannelName = kit.ChannelName ?? "",
            Tagline = kit.Tagline ?? "",
            Logo = Bare(kit.Logo),
            Sting = Bare(kit.Sting),
            Music = Bare(kit.Music),
            Mascot = Bare(kit.Mascot),
            Captions = kit.Captions is { } style && AssCaptions.IsHexColour(style.AccentColor) ? style : new CaptionStyle(),
            OutroSeconds = Math.Clamp(kit.OutroSeconds, MinOutroSeconds, MaxOutroSeconds),
            Layout = Enum.IsDefined(kit.Layout) ? kit.Layout : IntroLayout.Side,
            Structure = BeatPlan.Structures(show).Contains(kit.Structure) ? kit.Structure : BeatPlan.Structures(show)[0],
        };

        static string? Bare(string? name) => name is not null && name.Length > 0 && Path.GetFileName(name) == name ? name : null;
    }

    public void Save(string folder) => JsonFile.Save(Path.Combine(folder, FileName), this, ProjectStore.JsonOptions);

    /// <summary>Copies a file the user picked into the kit as its <paramref name="role"/>, and gives the name to keep.</summary>
    public static string Import(string folder, string file, string role)
    {
        var name = role + Path.GetExtension(file).ToLowerInvariant();
        var copy = Path.Combine(folder, name);
        Directory.CreateDirectory(folder);
        if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase))
            File.Copy(file, copy, overwrite: true);
        return name;
    }

    /// <summary>Where one of the kit's files is, or null when it has none or the file has gone.</summary>
    public static string? PathOf(string folder, string? name) =>
        name is not null && Path.GetFileName(name) == name && File.Exists(Path.Combine(folder, name)) ? Path.Combine(folder, name) : null;
}
