using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PoRedoMedia.Api.Common.Health;

/// <summary>One health report and when it was made.</summary>
public sealed record CachedHealthReport(HealthReport Report, DateTimeOffset CheckedAt, double CheckedMs);

/// <summary>
/// The one health report <c>/health</c> and <c>/api/diag</c> both read. The checks call outside services (Gemini,
/// Azure OpenAI, Key Vault, sign-in…), so they run at most once every <see cref="CacheFor"/>; every reader in that
/// minute gets the same report, with the same <see cref="CachedHealthReport.CheckedAt"/>. Each check has its own
/// 10-second timeout (<see cref="HealthChecks.AddPoHealthChecks"/>), so a refresh never hangs.
/// </summary>
public sealed class HealthReportCache(HealthCheckService health, TimeProvider clock) : IDisposable
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CachedHealthReport? _last;

    public async Task<CachedHealthReport> GetAsync(CancellationToken cancellationToken)
    {
        if (Fresh() is { } fresh)
            return fresh;

        // One refresh at a time; whoever waited reads what it made. The shared run is not tied to one caller's
        // request, so a caller that gives up cannot cancel it for the others.
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (Fresh() is { } made)
                return made;

            var started = Stopwatch.GetTimestamp();
            var report = await health.CheckHealthAsync(CancellationToken.None);
            _last = new CachedHealthReport(report, clock.GetUtcNow(),
                Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1));
            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private CachedHealthReport? Fresh() =>
        _last is { } last && clock.GetUtcNow() - last.CheckedAt < CacheFor ? last : null;
}
