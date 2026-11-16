using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Comics;

/// <summary>
/// How a comic is made, read from the same meme mix and narrator choice Create Movie uses: how many meme sounds and how
/// loud, how many laughs, the humour style, how big the lettering is, the narrator's voice and pace, and the heckler (an
/// offscreen voice wisecracking in the pauses) with its meanness, swearing and how often it speaks.
/// </summary>
public sealed record ComicOptions(MemeMix Mix, NarratorChoice Narrator, bool Heckler)
{
    public static readonly ComicOptions Default = new(ComicRequest.DefaultMix, NarratorChoice.Default, true);

    /// <summary>The narrator's voice for the usual warm style.</summary>
    public const string WarmVoice = "fable";

    public static ComicOptions From(ComicRequest request) => new(
        MemeMix.Parse(request.Mix) ?? ComicRequest.DefaultMix,
        NarratorChoice.Parse(request.Narrator),
        request.Heckler);

    /// <summary>Panels that may have a meme sound, by the meme sounds level (Off to Max).</summary>
    public int MaxSounds => Mix.Sounds switch { <= 0 => 0, 1 => 1, 2 => 2, 3 => 4, _ => ComicRequest.MaxPanels };

    /// <summary>Punchlines a studio audience laughs at, by the laugh track level (Off, Light, Heavy).</summary>
    public int MaxLaughs => Mix.Laughs switch { <= 0 => 0, 1 => 3, _ => 6 };

    /// <summary>Moments the audience claps or cheers at, by the same level: a win, a comeback, the ending.</summary>
    public int MaxApplause => Mix.Laughs switch { <= 0 => 0, 1 => 1, _ => 2 };

    /// <summary>Panels the heckler speaks on, out of <paramref name="panels"/>, by the roast lines level; none without a heckler.</summary>
    public int MaxHeckles(int panels) => !Heckler ? 0 : Mix.Jokes switch
    {
        <= 0 => 0,
        1 => (panels + 3) / 4,
        2 => (int)Math.Ceiling(panels * 0.5),
        3 => (int)Math.Ceiling(panels * 0.75),
        _ => panels,
    };

    /// <summary>The meme sounds' loudness under the voices, from the sound volume level.</summary>
    public double SoundGain => 0.6 * Math.Pow(10, MemeMix.VolumeDb[Math.Clamp(Mix.SoundVolume, 0, MemeMix.Max)] / 20);

    /// <summary>How big the lettering is drawn, from the caption size level (Normal is 1).</summary>
    public float TextScale => new[] { 0.75f, 0.87f, 1f, 1.15f, 1.3f }[Math.Clamp(Mix.CaptionSize, 0, MemeMix.Max)];

    public (string Voice, string Delivery) NarratorVoice => NarratorVoices.For(Narrator.Style, WarmVoice);

    /// <summary>The heckler's voice: one the narrator is not using.</summary>
    public string HecklerVoice => NarratorVoice.Voice == "verse" ? "ballad" : "verse";

    /// <summary>Voices kept for the narrator and the heckler, which the characters do not get.</summary>
    public IReadOnlyCollection<string> Reserved => Heckler ? [NarratorVoice.Voice, HecklerVoice] : [NarratorVoice.Voice];

    /// <summary>How the heckler delivers a line.</summary>
    public string HecklerDelivery =>
        $"You are an offscreen heckler in a comedy club, {MemeMix.MeannessNames[Math.Clamp(Mix.Meanness, 0, MemeMix.Max)].ToLowerInvariant()} in tone. "
        + "Quick, punchy, a little smug, landing the joke.";

    /// <summary>The humour the writer aims for, from the meme style; empty for Auto.</summary>
    public string Tone => Mix.Style switch
    {
        "sitcom" => "Play it like a family sitcom: warm, silly, every exchange setting up a punchline.",
        "drama" => "Play it like an overwrought TV drama: gasps, slow turns, everything treated as life or death.",
        "anime" => "Play it like an anime: dramatic reaction shots, inner monologues, speed lines and wild overreactions.",
        "brainrot" => "Play it with chaotic internet brainrot humour: absurd escalations and very online reactions.",
        "mlg" => "Play it like an MLG montage: over-the-top hype, dramatic zooms and everyone acting like a pro gamer.",
        "standard" => "Play it with classic internet meme humour.",
        _ => "",
    };

    private string Swearing => Mix.Swearing switch
    {
        <= 0 => "never swears",
        1 => "may use a mild curse now and then",
        2 => "swears sometimes",
        3 => "swears a lot, with real profanity",
        _ => "swears in every line, as filthy as a late-night roast",
    };

    private string Meanness => MemeMix.MeannessNames[Math.Clamp(Mix.Meanness, 0, MemeMix.Max)].ToLowerInvariant();

    /// <summary>
    /// How hard the whole comic goes at the people in it, by the meanness and swearing levels: the narrator, the heckler,
    /// and the characters' own trash talk. The lines it never crosses are said every time.
    /// </summary>
    public string RoastBrief =>
        $"The comic's humour is {Meanness}: roast the people for what they say and do (excuses, money troubles, bad plans, "
        + "pettiness, double standards, being late, being cheap, dodging the question), the way a comedy-club roast would. The "
        + $"narrator {Swearing} and insults them freely; the characters trash-talk each other at the same level when it fits who "
        + "they are. Make the insults specific to this conversation, never generic. Never mock anyone's appearance, body, age, "
        + "race, gender, sexuality, religion, disability or health; no slurs, no sexual content, no threats, and never a child.";

    /// <summary>The same comic a notch milder, for when the chat service's filter refuses the request; null when already mild.</summary>
    public ComicOptions? Softer() => Mix.Meanness > 2 || Mix.Swearing > 1
        ? this with { Mix = Mix with { Meanness = Math.Min(Mix.Meanness, 2), Swearing = Math.Min(Mix.Swearing, 1) } }
        : null;

    /// <summary>What the writer is told about the heckler, or that there is none.</summary>
    public string HecklerBrief(int panels)
    {
        var most = MaxHeckles(panels);
        if (most == 0)
            return "panels[].heckle: always empty (no heckler in this comic).";
        var target = Mix.Target switch
        {
            "adults" => "the grown-ups in the story",
            "narrator" => "the narrator",
            "things" => "the objects and situations",
            "camera" => "the way the scene is drawn",
            _ => "whatever is funniest",
        };
        return $"panels[].heckle: an offscreen heckler's insult about what was just said, at most 12 words, said after the panel's "
            + $"lines; on {most} panel(s) at most, the rest empty. The heckler is {Meanness}, {Swearing}, picks on {target}, and "
            + "keeps to the same lines as the rest of the comic.";
    }
}
