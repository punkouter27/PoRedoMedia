using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using PoRedoMedia.Api.Features.Movies;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Publishing;

/// <summary>
/// Connecting a user's YouTube and TikTok accounts (OAuth sign-in, the refresh token kept encrypted per user,
/// Disconnect revokes and deletes it) and what is connected. Publishing a movie itself is a movie job
/// (MovieStudio.Publish, POST /api/movies/{id}/publish). Each site is offered only when its app keys are configured;
/// in the Test environment (mock AI) both are stand-ins and nothing leaves the server.
/// </summary>
public static class PublishingEndpoints
{
    public const string HttpClientName = "Publishing";

    public static IServiceCollection AddPublishing(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton<PlatformTokenStore>();
        services.AddSingleton<ConnectStates>();
        // Uploads go a chunk at a time; a chunk on a slow line still finishes well inside this.
        services.AddHttpClient(HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(10));
        if (MockAi.IsEnabled(configuration, environment))
        {
            services.AddSingleton<IYouTubePublisher, FakeYouTubePublisher>();
            services.AddSingleton<ITikTokPublisher, FakeTikTokPublisher>();
            return services;
        }
        services.AddSingleton<IYouTubePublisher>(s => new GoogleYouTubePublisher(
            configuration[MovieKeys.YouTubeClientId], configuration[MovieKeys.YouTubeClientSecret],
            () => s.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));
        services.AddSingleton<ITikTokPublisher>(s => new TikTokPublisher(
            configuration[ConfigKeys.TikTokClientKey], configuration[ConfigKeys.TikTokClientSecret],
            string.Equals(configuration[ConfigKeys.TikTokDirectPost], "true", StringComparison.OrdinalIgnoreCase),
            () => s.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));
        return services;
    }

    public static IEndpointRouteBuilder MapPublishing(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/publish");
        group.MapGet("/status", async (ClaimsPrincipal user, IYouTubePublisher youTube, ITikTokPublisher tikTok, PlatformTokenStore tokens, CancellationToken ct) =>
            TypedResults.Ok(await StatusAsync(UserId.From(user), youTube, tikTok, tokens, ct)));

        // A browser navigation: off to the site's sign-in, which comes back to the callback below.
        group.MapGet("/{platform}/connect", (string platform, HttpRequest request, ClaimsPrincipal user, IYouTubePublisher youTube, ITikTokPublisher tikTok, ConnectStates states) =>
        {
            if (!TryPlatform(platform, out var site)) return Results.NotFound();
            if (!(site == Platform.YouTube ? youTube.Configured : tikTok.Configured)) return Results.Redirect(Back(site, "That site is not set up on this server yet."));
            var state = states.Begin(UserId.From(user), site);
            var redirect = Callback(request, site);
            return Results.Redirect((site == Platform.YouTube ? youTube.SignInPage(redirect, state) : tikTok.SignInPage(redirect, state)).AbsoluteUri);
        });

        group.MapGet("/{platform}/callback", async (string platform, string? code, string? state, string? error, HttpRequest request, ClaimsPrincipal user,
            IYouTubePublisher youTube, ITikTokPublisher tikTok, ConnectStates states, PlatformTokenStore tokens, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!TryPlatform(platform, out var site)) return Results.NotFound();
            var owner = UserId.From(user);
            if (state is null || !states.Finish(state, owner, site))
                return Results.Redirect(Back(site, "That sign-in did not start here, or took too long. Try Connect again."));
            if (error is not null || code is null)
                return Results.Redirect(Back(site, "The sign-in was cancelled, or the site said no."));
            try
            {
                var redirect = Callback(request, site);
                var got = site == Platform.YouTube ? await youTube.ExchangeAsync(code, redirect, ct) : await tikTok.ExchangeAsync(code, redirect, ct);
                if (got.Refresh is not { Length: > 0 } refresh)
                    return Results.Redirect(Back(site, "The site gave no lasting connection. Remove the app's access in your account settings and connect again."));
                await tokens.SaveAsync(owner, site, refresh, ct);
                return Results.Redirect($"/movie?connected={site}");
            }
            catch (Exception e) when (e is PublishException or HttpRequestException)
            {
                logs.CreateLogger(typeof(PublishingEndpoints)).LogWarning("Connecting {Platform} failed: {Type}", site, e.GetType().Name);
                return Results.Redirect(Back(site, e is PublishException p ? p.Message : "The site could not be reached. Try again."));
            }
        });

        group.MapPost("/{platform}/disconnect", async (string platform, ClaimsPrincipal user, IYouTubePublisher youTube, ITikTokPublisher tikTok,
            PlatformTokenStore tokens, CancellationToken ct) =>
        {
            if (!TryPlatform(platform, out var site)) return Results.NotFound();
            var owner = UserId.From(user);
            if (await tokens.GetAsync(owner, site, ct) is { } refresh)
            {
                try
                {
                    await (site == Platform.YouTube ? youTube.RevokeAsync(refresh, ct) : tikTok.RevokeAsync(refresh, ct));
                }
                catch (HttpRequestException)
                {
                    // Revoking is a courtesy: the token is deleted here whatever the site says.
                }
            }
            await tokens.DeleteAsync(owner, site, ct);
            return Results.Ok(await StatusAsync(owner, youTube, tikTok, tokens, ct));
        }).RequireAntiforgeryValidation();
        return app;
    }

    public static async Task<PublishStatusDto> StatusAsync(UserId owner, IYouTubePublisher youTube, ITikTokPublisher tikTok, PlatformTokenStore tokens, CancellationToken ct) => new(
        new(youTube.Configured, youTube.Configured && await tokens.GetAsync(owner, Platform.YouTube, ct) is not null, false,
            youTube.Configured ? null : "YouTube publishing needs this server's Google OAuth client: set YouTube:ClientId and YouTube:ClientSecret (see docs/publishing-setup.md)."),
        new(tikTok.Configured, tikTok.Configured && await tokens.GetAsync(owner, Platform.TikTok, ct) is not null, tikTok.DirectPost,
            tikTok.Configured ? null : "TikTok publishing needs this server's TikTok app: set TikTok:ClientKey and TikTok:ClientSecret (see docs/publishing-setup.md)."));

    private static bool TryPlatform(string text, out Platform platform) => Enum.TryParse(text, ignoreCase: true, out platform) && Enum.IsDefined(platform);

    private static string Callback(HttpRequest request, Platform site) => $"{request.Scheme}://{request.Host}/api/publish/{site.ToString().ToLowerInvariant()}/callback";

    private static string Back(Platform site, string problem) => $"/movie?connectError={Uri.EscapeDataString($"{site}: {problem}")}";

    private static RouteHandlerBuilder RequireAntiforgeryValidation(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<AntiforgeryValidationFilter>();
}

/// <summary>
/// Sign-ins under way: a random state handed to the site and checked when it comes back, tied to the user and site,
/// good for ten minutes and once. ponytail: in memory, so a sign-in does not survive a restart or a second instance.
/// </summary>
public sealed class ConnectStates(TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (string User, Platform Site, DateTimeOffset Until)> _pending = new();

    public string Begin(UserId user, Platform site)
    {
        foreach (var stale in _pending.Where(p => p.Value.Until < time.GetUtcNow()).Select(p => p.Key).ToList())
            _pending.TryRemove(stale, out _);
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _pending[state] = (user.Value, site, time.GetUtcNow() + Lifetime);
        return state;
    }

    /// <summary>True when the state was handed out to this user for this site and has not run out; it can be used once.</summary>
    public bool Finish(string state, UserId user, Platform site) =>
        _pending.TryRemove(state, out var pending) && pending.User == user.Value && pending.Site == site && pending.Until >= time.GetUtcNow();
}
