using Microsoft.Extensions.AI;
using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>A result's name and what it is, in a sentence, for the Library to show and search.</summary>
public sealed record MediaSummary(string Title, string Summary);

/// <summary>Names a run's result. A nice-to-have: callers must carry on without it.</summary>
public interface IMediaSummarizer
{
    /// <summary>Null when there was nothing to go on.</summary>
    Task<MediaSummary?> SummarizeAsync(MediaItem item, CancellationToken ct = default);
}

/// <summary>
/// One cheap chat call per result: the words the step already kept with it (lyrics, captions,
/// what the AI saw) and, for a picture or video, its small thumbnail.
/// </summary>
public sealed class ChatMediaSummarizer(IChatClient chat, BlobStorageService blobs) : IMediaSummarizer
{
    internal const int MaxTitleWords = 6;
    private const int MaxTitleLength = 80;
    private const int MaxSummaryLength = 240;
    private const int MaxContext = 1500;

    private const string SystemPrompt =
        "You name items in a media library. Given what a tool made and anything known about it, reply with "
        + "title: at most 6 words, specific to this item, no quotes, no trailing full stop; and summary: one "
        + "plain sentence on what it shows or says. Describe only what is given or visible. Do not describe or "
        + "infer race, ethnicity, age, body size, disability or attractiveness.";

    public async Task<MediaSummary?> SummarizeAsync(MediaItem item, CancellationToken ct = default)
    {
        var known = string.Join('\n', new[]
        {
            $"Made by: {item.Origin}",
            $"Current title: {item.Title}",
            item.Detail is { } detail ? $"What the AI saw: {detail}" : null,
            item.Text is { } text ? $"Its words: {text}" : null,
        }.OfType<string>());
        if (known.Length > MaxContext)
            known = known[..MaxContext];

        List<AIContent> content = [];
        if (item.Kind is MediaKind.Image or MediaKind.Video && await blobs.ExistsAsync(MediaBlobPaths.Thumbnail(item.Id), ct))
            content.Add(new DataContent(await blobs.ReadAllBytesAsync(MediaBlobPaths.Thumbnail(item.Id), ct), "image/jpeg"));
        content.Add(new TextContent(known));

        var response = await chat.GetResponseAsync<Answer>(
            [new ChatMessage(ChatRole.System, SystemPrompt), new ChatMessage(ChatRole.User, content)],
            new ChatOptions { MaxOutputTokens = 200 }, cancellationToken: ct);
        return response.TryGetResult(out var answer) ? Clean(answer.Title, answer.Summary) : null;
    }

    /// <summary>The answer made safe to store: a title of at most six words, one sentence. Null when either is empty.</summary>
    internal static MediaSummary? Clean(string? title, string? summary)
    {
        var words = (title ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxTitleWords);
        var cleanTitle = UserText.Clean(string.Join(' ', words).Trim('"', '\'', '.'), MaxTitleLength);
        var cleanSummary = UserText.Clean(summary, MaxSummaryLength);
        return cleanTitle is null || cleanSummary is null ? null : new MediaSummary(cleanTitle, cleanSummary);
    }

    private sealed record Answer(string? Title, string? Summary);
}

/// <summary>Keeps the title the step chose and says, with no AI call, that this is a mock.</summary>
public sealed class MockMediaSummarizer : IMediaSummarizer
{
    public Task<MediaSummary?> SummarizeAsync(MediaItem item, CancellationToken ct = default) =>
        Task.FromResult<MediaSummary?>(new(item.Title, $"A mock summary of {item.Title}, written with no AI call."));
}
