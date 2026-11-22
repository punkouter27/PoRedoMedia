namespace PoRedoMedia.Shared.Models;

/// <summary>
/// What <c>GET /api/diag</c> says about the server, for the <c>/diag</c> page. Plain words only: never a connection
/// string, key, address, path, id, email or exception message (an exception's type at most).
/// </summary>
/// <param name="Status">"Healthy", "Degraded" or "Unhealthy": the worst of <paramref name="Checks"/>.</param>
/// <param name="Version">The build's YYMMDD stamp.</param>
/// <param name="CheckedMs">How long all the checks took together, in milliseconds.</param>
public sealed record DiagReportDto(string Status, string Environment, string Version, DateTimeOffset CheckedAt, double CheckedMs, DiagCheckDto[] Checks);

/// <summary>One dependency on <c>/diag</c>.</summary>
/// <param name="Group">"api" (API), "data" (Data) or "service" (Services): its section on <c>/diag</c>.</param>
/// <param name="Status">"Healthy", "Degraded", "Unhealthy", or "Off" for a service switched off on purpose (not set up
/// here, or a stand-in in use): it is not called and does not count toward the overall status.</param>
/// <param name="Purpose">What the app uses it for, in one line.</param>
/// <param name="Detail">How it is, in one line.</param>
/// <param name="Problem">The type of the exception behind a failure, if there was one.</param>
public sealed record DiagCheckDto(string Name, string Group, string Label, string Status, string Purpose, string Detail, double LatencyMs, string? Problem);
