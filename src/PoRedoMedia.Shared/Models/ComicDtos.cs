namespace PoRedoMedia.Shared.Models;

/// <summary>What Comic-ify can do on this server. <see cref="Notice"/> says what is missing, when something is.</summary>
public sealed record ComicConfigDto(bool Available, bool Voices, string? Notice);

/// <summary>A pasted conversation (texts, a chat, an email thread) to turn into a comic.</summary>
public sealed record ComicRequest(string Conversation)
{
    public const int MinLength = 20, MaxLength = 8000;
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
