namespace PoRedoMedia.Shared.Models;

/// <summary>
/// How Meme-ify treats a video: how mean and how sweary the roast is and how many lines it gets, how many meme sounds and
/// pictures, and the finer settings (style, sound volume, chaos, roast target, sound fame, caption size, turn-taking,
/// laugh track). Every level runs from 0 to its names' length less one; the defaults are what Meme-ify has always done.
/// </summary>
/// <remarks>
/// It travels as text (<see cref="Encode"/>) in the <see cref="RunOptions.VideoMix"/> run option and on a movie; five
/// digits alone (the first form) still read. A child is never the target of an insult at any level.
/// </remarks>
public sealed record MemeMix(
    int Meanness, int Swearing, int Jokes, int Sounds, int Pictures,
    string Style = "auto", int SoundVolume = 2, int Chaos = 2, string Target = "mix", int Fame = 3,
    int CaptionSize = 2, bool CaptionsTop = false, string Turns = "room", int Laughs = 0)
{
    public const int Max = 4;

    /// <summary>"Lots": the amount of roast lines and meme sounds Meme-ify has always added. Max adds more.</summary>
    public const int Usual = 3;

    /// <summary>The usual mix: a sarcastic roast without swearing, a roast line every seven seconds, the usual meme sounds and pictures.</summary>
    public static readonly MemeMix Default = new(2, 0, Usual, Usual, 2);

    /// <summary>A kids' video: the meanest, sweariest roast, the usual meme sounds and pictures, and a light laugh track.</summary>
    public static readonly MemeMix Kids = new(4, 4, Usual, Usual, 2, Laughs: 1);

    /// <summary>The mix the old on/off "crude" switch stood for.</summary>
    public static MemeMix FromCrude(bool crude) => crude ? Default with { Meanness = 3, Swearing = 4 } : Default;

    public static MemeMix For(string? show) => show == "Kids" ? Kids : Default;

    public static readonly string[] MeannessNames = ["Gentle", "Teasing", "Sarcastic", "Savage", "Brutal"];
    public static readonly string[] SwearingNames = ["None", "Mild", "Some", "Lots", "Every line"];
    public static readonly string[] AmountNames = ["Off", "Few", "Some", "Lots", "Max"];
    public static readonly string[] VolumeNames = ["Quiet", "Soft", "Normal", "Loud", "Blasting"];
    public static readonly string[] ChaosNames = ["None", "Light", "Normal", "Wild", "Deep-fried"];
    public static readonly string[] FameNames = ["Deep cuts", "Lesser-known", "Balanced", "Famous first", "Only famous"];
    public static readonly string[] CaptionSizeNames = ["Small", "Smaller", "Normal", "Large", "Huge"];
    public static readonly string[] LaughNames = ["Off", "Light", "Heavy"];

    /// <summary>Meme-ify styles (the director's personas); "auto" is the series' own (sitcom for kids, standard otherwise).</summary>
    public static readonly (string Id, string Name)[] Styles =
        [("auto", "Auto"), ("standard", "Internet"), ("brainrot", "Brainrot"), ("mlg", "MLG"), ("sitcom", "Sitcom"), ("drama", "Drama"), ("anime", "Anime")];

    /// <summary>What the roast picks on most.</summary>
    public static readonly (string Id, string Name)[] Targets =
        [("mix", "Everything"), ("adults", "The grown-ups"), ("narrator", "The narrator"), ("things", "Toys & things"), ("camera", "The filming")];

    /// <summary>How the roast and a narrator share the floor (Create Movie).</summary>
    public static readonly (string Id, string Name, string Help)[] TurnModes =
    [
        ("room", "Make room", "Each film holds its last frame a few seconds after the narration, so the roast always gets its lines in."),
        ("polite", "Polite", "The roast speaks only in pauses that fit a whole joke; a joke with no room is left out."),
        ("interrupt", "Interrupt", "The roast may cut in over the narrator, who drops under it."),
    ];

    /// <summary>Meme caption font size, by <see cref="CaptionSize"/>; Normal is the size captions have always had.</summary>
    public static readonly int[] CaptionFontSizes = [26, 30, 36, 46, 58];

    /// <summary>Change to the meme sounds' loudness in dB, by <see cref="SoundVolume"/>.</summary>
    public static readonly double[] VolumeDb = [-10, -5, 0, 4, 8];

    /// <summary>Most seconds a film holds its last frame after its narration to make room for the roast; whatever goes unused is cut.</summary>
    public const double RoomSeconds = 6;

    public string Encode() => FormattableString.Invariant(
        $"{Meanness}{Swearing}{Jokes}{Sounds}{Pictures}|{Style}|{SoundVolume}{Chaos}{Fame}{CaptionSize}{Laughs}|{Target}|{Turns}|{(CaptionsTop ? 1 : 0)}");

    /// <summary>
    /// The mix this text stands for; null when its five levels cannot be read. A setting that is missing or not known
    /// keeps its default, and a level over its top counts as the top.
    /// </summary>
    public static MemeMix? Parse(string? text)
    {
        var parts = (text ?? "").Split('|');
        if (!Digits(parts[0], 5, out var d))
            return null;
        var mix = new MemeMix(Clamp(d[0], Max), Clamp(d[1], Max), Clamp(d[2], Max), Clamp(d[3], Max), Clamp(d[4], Max));
        if (parts.Length > 1 && Styles.Any(s => s.Id == parts[1]))
            mix = mix with { Style = parts[1] };
        if (parts.Length > 2 && Digits(parts[2], 5, out var e))
            mix = mix with { SoundVolume = Clamp(e[0], Max), Chaos = Clamp(e[1], Max), Fame = Clamp(e[2], Max), CaptionSize = Clamp(e[3], Max), Laughs = Clamp(e[4], LaughNames.Length - 1) };
        if (parts.Length > 3 && Targets.Any(t => t.Id == parts[3]))
            mix = mix with { Target = parts[3] };
        if (parts.Length > 4 && TurnModes.Any(t => t.Id == parts[4]))
            mix = mix with { Turns = parts[4] };
        if (parts.Length > 5)
            mix = mix with { CaptionsTop = parts[5] == "1" };
        return mix;

        static int Clamp(int value, int top) => Math.Min(value, top);
    }

    private static bool Digits(string text, int count, out int[] digits)
    {
        digits = text.Length == count && text.All(char.IsAsciiDigit) ? [.. text.Select(c => c - '0')] : [];
        return digits.Length == count;
    }

    /// <summary>A crude roast, as the roast summary says it.</summary>
    public bool Crude => Swearing >= 3 || Meanness >= 4;
}

/// <summary>
/// The narrator's voice for a movie: a style (which voice speaks, and how) and a pace. Stored on each clip as
/// <see cref="Encode"/>; null or unreadable is the warm storyteller at normal pace, as narration has always been.
/// </summary>
public sealed record NarratorChoice(string Style = "warm", int Pace = 2)
{
    public static readonly NarratorChoice Default = new();

    public static readonly (string Id, string Name, string Help)[] Styles =
    [
        ("warm", "Warm storyteller", "Clear, warm and lively: the usual narrator."),
        ("excited", "Excited kids' host", "Bright, bouncy and amazed by everything, like a children's TV host."),
        ("deadpan", "Deadpan documentary", "Low, dry and serious, like a nature documentary, which makes the silly bits funnier."),
        ("sports", "Sports commentator", "Fast and breathless, calling every moment like the last seconds of a big game."),
    ];

    public static readonly string[] PaceNames = ["Slow", "Relaxed", "Normal", "Brisk", "Fast"];

    /// <summary>Speaking speed, by <see cref="Pace"/>.</summary>
    public static readonly double[] PaceRates = [0.85, 0.93, 1.0, 1.08, 1.18];

    public double Rate => PaceRates[Math.Clamp(Pace, 0, PaceRates.Length - 1)];

    /// <summary>"style@rate", as a movie's clips keep it (the engine reads the rate as a number).</summary>
    public string Encode() => FormattableString.Invariant($"{Style}@{Rate:0.##}");

    public static NarratorChoice Parse(string? text)
    {
        var parts = (text ?? "").Split('@');
        var style = Styles.Any(s => s.Id == parts[0]) ? parts[0] : "warm";
        var rate = parts.Length > 1 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 1.0;
        // The pace whose rate is nearest the one kept.
        var pace = Enumerable.Range(0, PaceRates.Length).MinBy(i => Math.Abs(PaceRates[i] - rate));
        return new(style, pace);
    }
}
