using Microsoft.Extensions.AI;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>A critic's verdict on a roast draft: 1 to 10, and what would make it hit harder.</summary>
public sealed record RoastCritique(double Score, string Critique);

/// <summary>Scores a written roast for punch and specificity. One cheap call; never decides alone what is kept.</summary>
public interface IRoastCritic
{
    /// <summary>Null when no verdict could be had; the draft then stands.</summary>
    Task<RoastCritique?> CritiqueAsync(string task, string draft, CancellationToken ct = default);
}

public sealed partial class ChatRoastCritic(IChatClient chat, ILogger<ChatRoastCritic> logger) : IRoastCritic
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "The roast critic gave no verdict; the draft stands")]
    private partial void LogFailed(Exception ex);

    private const string SystemPrompt =
        "You are a head writer reviewing a roast before it goes on stage. Score it from 1 to 10 for PUNCH (every line "
        + "lands a real joke with a turn, not a description) and SPECIFICITY (the jokes could only be about this subject, "
        + "not any other). A list of details with no punchlines scores 3 or less. Then give the critique: one or two "
        + "sentences naming the weakest lines and exactly what would make them hit harder. Never ask for jokes about "
        + "race, ethnicity, religion, gender, sexuality, disability, age or body size.";

    public async Task<RoastCritique?> CritiqueAsync(string task, string draft, CancellationToken ct = default)
    {
        try
        {
            var response = await chat.GetResponseAsync<Answer>(
            [
                new ChatMessage(ChatRole.System, SystemPrompt),
                new ChatMessage(ChatRole.User, $"TASK: {task}\nDRAFT:\n{draft}"),
            ], new ChatOptions { MaxOutputTokens = 300 }, cancellationToken: ct);

            return response.TryGetResult(out var answer) && answer.Score is >= 1 and <= 10
                ? new RoastCritique(answer.Score, UserText.Clean(answer.Critique, 600) ?? "")
                : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogFailed(ex);
            return null;
        }
    }

    private sealed record Answer(double Score, string? Critique);
}

/// <summary>Passes every draft, with no AI call, so a mock run never rewrites.</summary>
public sealed class MockRoastCritic : IRoastCritic
{
    public Task<RoastCritique?> CritiqueAsync(string task, string draft, CancellationToken ct = default) =>
        Task.FromResult<RoastCritique?>(new(RoastQuality.PassScore + 2, "A mock critique, written with no AI call."));
}

/// <summary>The second pass both roasts take: score the draft, and below the bar rewrite it once with the critique.</summary>
public static class RoastQuality
{
    /// <summary>A draft scoring below this is rewritten.</summary>
    public const double PassScore = 6;

    /// <summary>
    /// The draft, or one rewrite of it when the critic scored it below <see cref="PassScore"/>. At most
    /// one extra call to the writer, however the rewrite scores. Any failure keeps the draft.
    /// </summary>
    /// <param name="rewrite">Writes the draft again from the critique; null or empty when it could not.</param>
    public static async Task<(string Text, RoastCritique? Critique, bool Rewritten)> ImproveOnceAsync(
        IRoastCritic? critic, string task, string draft, Func<RoastCritique, CancellationToken, Task<string?>> rewrite,
        ILogger logger, CancellationToken ct)
    {
        if (critic is null || await critic.CritiqueAsync(task, draft, ct) is not { } critique)
            return (draft, null, false);
        if (critique.Score >= PassScore)
            return (draft, critique, false);

        try
        {
            var better = await rewrite(critique, ct);
            return string.IsNullOrWhiteSpace(better) ? (draft, critique, false) : (better, critique, true);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The roast rewrite failed; keeping the draft the critic scored {Score}", critique.Score);
            return (draft, critique, false);
        }
    }

    /// <summary>What the writer is told when it gets a second go.</summary>
    public static string RewriteBrief(RoastCritique critique, string draft) =>
        $"A head writer scored your draft {critique.Score:0}/10 for punch and specificity. Their note: {critique.Critique}\n"
        + $"Your draft was:\n{draft}\n\nRewrite it to fix exactly that, keeping the same format and every rule above.";
}
