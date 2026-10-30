using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.Shared.Models;

public enum RunStatus
{
    Queued,
    Running,
    Complete,

    /// <summary>A step failed. Outputs made before it are kept; <c>Error</c> says what went wrong.</summary>
    Failed,
}

/// <summary>Apply these functions to this source. Options are keyed <c>function.option</c>.</summary>
public sealed record RunRequest(Guid SourceId, MediaFunction[] Functions, Dictionary<string, string>? Options = null);

public sealed record RunDto(
    Guid Id,
    Guid SourceId,
    MediaFunction[] Functions,
    RunStatus Status,
    MediaFunction? CurrentStep,
    string? Error,
    Guid[] OutputIds,
    string[] Notes,
    DateTimeOffset CreatedAt,
    Dictionary<string, string>? Options = null,
    string[]? Log = null,
    StepTelemetryDto[]? Steps = null);

/// <summary>
/// What one step of a run took: wall-clock seconds, and the AI calls it made as the meter saw
/// them. <c>Tokens</c> counts only token-billed calls; <c>CostUsd</c> is an estimate from list
/// prices, null when nothing it called had a price.
/// </summary>
public sealed record StepTelemetryDto(MediaFunction Function, double Seconds, int AiCalls, long Tokens, decimal? CostUsd);

/// <summary>One progress event for a run, pushed over the hub. <c>Steps</c> holds the steps finished so far.</summary>
public sealed record RunProgressDto(
    Guid RunId,
    RunStatus Status,
    MediaFunction? Step,
    int Percent,
    string Message,
    MediaDto? Output = null,
    StepTelemetryDto[]? Steps = null);
