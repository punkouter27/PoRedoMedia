namespace PoRedoMedia.Api.Common;

public static class HealthEndpoint
{
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        // Liveness only: answers as soon as the process serves requests, with no dependency checks.
        app.MapGet("/health/live", () => Results.Text("Healthy")).AllowAnonymous();

        // Readiness: the process plus its one dependency, the storage account. Same pair of routes as every Po app.
        app.MapGet("/health", async (StorageClients storage, CancellationToken ct) =>
        {
            try
            {
                await storage.PingAsync(ct);
                return Results.Text("Healthy");
            }
            catch (Exception)
            {
                return Results.Text("Unhealthy: storage is not reachable", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).AllowAnonymous();
        return app;
    }
}
