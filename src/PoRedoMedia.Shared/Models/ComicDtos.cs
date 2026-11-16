namespace PoRedoMedia.Shared.Models;

/// <summary>What Comic-ify can do on this server. <see cref="Notice"/> says what is missing, when something is.</summary>
public sealed record ComicConfigDto(bool Available, bool Voices, string? Notice);

/// <summary>
/// A pasted conversation (texts, a chat, an email thread) to turn into a comic, with how to make it: the meme mix
/// (<see cref="MemeMix.Encode"/>), the narrator (<see cref="NarratorChoice.Encode"/>) and whether a heckler chimes in.
/// Missing settings take <see cref="DefaultMix"/>, the warm narrator and the heckler.
/// </summary>
public sealed record ComicRequest(string Conversation, string? Mix = null, string? Narrator = null, bool Heckler = true)
{
    public const int MinLength = 20, MaxLength = 8000;

    /// <summary>The fewest and most panels a comic has.</summary>
    public const int MinPanels = 3, MaxPanels = 10;

    /// <summary>A comic's usual mix: a brutal roast that swears a lot, a heckler on most panels, the usual meme sounds, and a heavy laugh track with applause.</summary>
    public static readonly MemeMix DefaultMix = MemeMix.Default with { Meanness = 4, Swearing = 3, Jokes = 3, Laughs = 2 };

    /// <summary>
    /// What a comic should cost, about and at most, from the server's prices: one script, a picture a panel and a look at
    /// it to find the speakers, a voice a spoken part. The panels are guessed from the number of messages (about one and a half a panel); at most is every panel
    /// drawn twice and every part spoken.
    /// </summary>
    public static (decimal About, decimal AtMost) Estimate(string conversation, bool heckler, AiPricingDto prices)
    {
        var messages = conversation.Split('\n').Count(l => l.Trim().Length > 0);
        var panels = Math.Clamp((int)Math.Ceiling(messages / 1.5), MinPanels, MaxPanels);
        var spokenPerPanel = 2.5m + (heckler ? 0.5m : 0);
        var about = prices.TextUsd + panels * (prices.ImageUsd + prices.VisionUsd) + panels * spokenPerPanel * prices.SpeechUsd;
        var atMost = prices.TextUsd + MaxPanels * (2 * prices.ImageUsd + prices.VisionUsd) + MaxPanels * (4 + (heckler ? 1 : 0)) * prices.SpeechUsd;
        return (Math.Round(about, 2), Math.Round(atMost, 2));
    }
}

/// <summary>
/// The user's comic under way, or the one that last ended. <see cref="State"/> is "Working", "Done" or "Failed";
/// a finished comic is two Library items, the strip and its video.
/// </summary>
public sealed record ComicJobDto(
    string State, string Step, double Progress, string? Title, Guid? StripId, Guid? VideoId, string? Error, DateTimeOffset Started)
{
    public bool Working => State == "Working";
}
