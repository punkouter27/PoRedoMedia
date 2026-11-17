using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Common.Health;

/// <summary>
/// The same pair of probe routes as every Po app, which the deploy's health gate checks, plus <c>/api/diag</c> for the
/// <c>/diag</c> page.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>The build's YYMMDD stamp (Directory.Build.targets stamps the client assembly the server hosts).</summary>
    private static readonly string Version = typeof(PoRedoMedia.Client.Routes).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "BuildStamp")?.Value ?? "unknown";

    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        // Liveness: the process serves requests. No check runs, so it is 200 "Healthy" whatever the dependencies say.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();

        // Readiness: every dependency check. Unhealthy (storage unreachable) is 503; Degraded (an optional service
        // missing) is still 200, so the deploy gate fails only on what the app cannot run without.
        app.MapHealthChecks("/health").AllowAnonymous();

        // The same checks in plain words for /diag, which anyone may open: nothing identifying goes out (DiagReportDto).
        app.MapGet("/api/diag", async (HealthCheckService health, IHostEnvironment environment, CancellationToken ct) =>
        {
            var started = Stopwatch.GetTimestamp();
            var report = await health.CheckHealthAsync(ct);
            var checks = report.Entries.Select(e =>
            {
                var about = HealthChecks.About.TryGetValue(e.Key, out var known) ? known : (Group: "service", Label: e.Key, Purpose: "");
                return new DiagCheckDto(e.Key, about.Group, about.Label, e.Value.Status.ToString(), about.Purpose,
                    e.Value.Description ?? e.Value.Status.ToString(), Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                    e.Value.Exception?.GetType().Name);
            }).ToArray();
            return TypedResults.Ok(new DiagReportDto(report.Status.ToString(), environment.EnvironmentName, Version,
                DateTimeOffset.UtcNow, Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1), checks));
        }).AllowAnonymous();
        return app;
    }
}
