using System.Security.Claims;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Comics;

/// <summary>Comic-ify over HTTP: what the server can do, start a comic, and how the user's comic is coming along.</summary>
public static class ComicEndpoints
{
    public static IServiceCollection AddComics(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton<ISpeechSynthesis>(s => MockAi.IsEnabled(configuration, environment)
            ? new NoSpeech()
            : ActivatorUtilities.CreateInstance<AiFoundrySpeech>(s));
        services.AddSingleton<ComicWriter>();
        services.AddSingleton<ComicSpeakerLocator>();
        services.AddSingleton<ComicStudio>();
        return services;
    }

    public static IEndpointRouteBuilder MapComics(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/comics").RequireAntiforgeryValidation();

        group.MapGet("/config", (ComicStudio studio) => studio.Config());

        group.MapGet("/job", (ClaimsPrincipal user, ComicStudio studio) =>
            studio.Job(UserId.From(user)) is { } job ? Results.Ok(job) : Results.NoContent());

        group.MapPost("/", async (ComicRequest request, ClaimsPrincipal user, ComicStudio studio, CancellationToken ct) =>
        {
            var (job, error, status) = await studio.StartAsync(UserId.From(user), request, ct);
            return job is not null ? Results.Ok(job) : Results.Problem(detail: error, statusCode: status);
        });

        return app;
    }
}
