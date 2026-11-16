using Microsoft.Extensions.AI;
using SkiaSharp;

namespace PoRedoMedia.Api.Features.Comics;

/// <summary>
/// Finds where each speaker stands in a drawn panel, so a speech bubble's tail points at the person talking: the picture
/// model does not reliably put them in the order it was asked for. One small vision call a panel. Anyone not found (or
/// no chat model) gets no spot, and the lettering falls back to the order they speak in.
/// </summary>
public sealed partial class ComicSpeakerLocator(ILogger<ComicSpeakerLocator> logger, IChatClient? chat = null)
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "The speakers in a comic panel could not be found; their bubbles follow speaking order")]
    private partial void LogNotFound(Exception ex);

    private const string SystemPrompt =
        "You locate characters in a cartoon comic panel. You are given each character's name and look. For every one you "
        + "can see, give x: the horizontal centre of their head, and y: the top of their head, each as a fraction of the "
        + "picture's width or height (0 is the left or top edge, 1 the right or bottom). Leave out anyone you cannot see. "
        + "Never give two characters the same person.";

    /// <returns>Each speaker found, by name, at their head as fractions of the picture (0–1).</returns>
    public async Task<IReadOnlyDictionary<string, SKPoint>> LocateAsync(byte[] art, IReadOnlyList<ComicCharacter> speakers, CancellationToken ct)
    {
        if (chat is null || speakers.Count == 0)
            return new Dictionary<string, SKPoint>();
        try
        {
            var cast = string.Join("\n", speakers.Select(c => $"- {c.Name}: {c.Look}"));
            var response = await chat.GetResponseAsync<Answer>(
            [
                new ChatMessage(ChatRole.System, SystemPrompt),
                new ChatMessage(ChatRole.User, [new DataContent(ImageBytes.ToJpeg(art, 768), "image/jpeg"), new TextContent($"The characters:\n{cast}")]),
            ], cancellationToken: ct);
            return response.TryGetResult(out var answer) ? Spots(answer, speakers) : new Dictionary<string, SKPoint>();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogNotFound(e);
            return new Dictionary<string, SKPoint>();
        }
    }

    /// <summary>The answer's spots for the speakers asked about, kept inside the picture; a name not asked about is dropped.</summary>
    internal static Dictionary<string, SKPoint> Spots(Answer answer, IReadOnlyList<ComicCharacter> speakers)
    {
        var spots = new Dictionary<string, SKPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var spot in answer.People ?? [])
        {
            if (spot.Name is not { } name || spot.X is not { } x || spot.Y is not { } y
                || speakers.FirstOrDefault(c => c.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) is not { } who)
                continue;
            spots[who.Name] = new SKPoint(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        }
        return spots;
    }

    internal sealed record Answer(Person[]? People);

    internal sealed record Person(string? Name, float? X, float? Y);
}
