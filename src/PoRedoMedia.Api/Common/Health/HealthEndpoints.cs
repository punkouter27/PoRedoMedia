using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Common.Health;

/// <summary>
/// The probe routes the deploy's health gate checks (<c>/health/live</c>, with the <c>X-Commit</c> header set in
/// Program.cs) and <c>/health</c>, plus <c>/api/diag</c> for the <c>/diag</c> page.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>The build's YYMMDD version (the BuildVersion metadata Directory.Build.props stamps on every assembly).</summary>
    private static readonly string Version = typeof(HealthEndpoints).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "BuildVersion")?.Value ?? "unknown";

    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        // Liveness: the process serves requests. No check runs, so it is 200 "Healthy" whatever the dependencies say.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();

        // Readiness: every dependency check. Unhealthy (storage unreachable) is 503; Degraded (an optional service
        // missing) is still 200, so the deploy gate fails only on what the app cannot run without.
        app.MapHealthChecks("/health").AllowAnonymous();

        // The same checks in plain words for /diag, which anyone may open: nothing identifying goes out (DiagReportDto).
        app.MapGet("/api/diag", DiagAsync).AllowAnonymous();
        return app;
    }

    private static async Task<Ok<DiagReportDto>> DiagAsync(HealthCheckService health, IHostEnvironment environment, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var report = await health.CheckHealthAsync(ct);
        var checks = report.Entries.Select(e =>
        {
            var about = HealthChecks.About.TryGetValue(e.Key, out var known) ? known : (Group: "service", Label: e.Key, Purpose: "");
            var detail = e.Value.Description ?? e.Value.Status.ToString();
            // A service switched off on purpose is "Off" (neutral), not Healthy: it was not called (ServiceProbe.Off).
            var status = e.Value.Data.ContainsKey(ServiceProbe.OffKey) ? "Off" : e.Value.Status.ToString();
            // Problem is set only when the check is not Healthy: the same plain words, never an exception message.
            return new DiagCheckDto(e.Key, about.Group, about.Label, status, about.Purpose,
                detail, Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                e.Value.Status == HealthStatus.Healthy ? null : detail);
        }).ToArray();
        return TypedResults.Ok(new DiagReportDto(report.Status.ToString(), environment.EnvironmentName, Version,
            DateTimeOffset.UtcNow, Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1), checks));
    }
}
