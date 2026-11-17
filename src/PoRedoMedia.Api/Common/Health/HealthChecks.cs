using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoRedoMedia.Api.Configuration;
using PoRedoMedia.Api.Features.Movies;

namespace PoRedoMedia.Api.Common.Health;

/// <summary>
/// One <see cref="IHealthCheck"/> per thing the app depends on. Only storage can fail <c>/health</c> (503): it holds
/// every user's items, runs and settings. The rest report <see cref="HealthStatus.Degraded"/> when missing, which
/// <c>/health</c> still answers 200 for, because the app runs without them and offers less.
/// </summary>
public static class HealthChecks
{
    /// <summary>What each check is, for people: its section on <c>/diag</c>, its name there and what it is for.</summary>
    public static readonly IReadOnlyDictionary<string, (string Group, string Label, string Purpose)> About =
        new Dictionary<string, (string, string, string)>
        {
            ["storage"] = ("storage", "Storage account", "Holds every Library item, run, share link and setting."),
            ["movie-files"] = ("storage", "Movie folder", "Holds Create Movie's films and renders on this server while a movie is being made."),
            ["ffmpeg"] = ("service", "FFmpeg", "Renders every video, GIF, comic and movie."),
            ["google-ai"] = ("service", "Google AI (Gemini)", "Looks at pictures, draws images, makes AI video and music."),
            ["azure-openai"] = ("service", "Azure OpenAI", "Writes captions, roasts and movie scripts; a second opinion for pictures."),
            ["ai-foundry"] = ("service", "Azure AI Foundry", "Voices, speech-to-text and FLUX pictures."),
            ["key-vault"] = ("service", "Key Vault", "Where the deployed app's keys are kept; read once at startup."),
            ["sign-in"] = ("service", "Microsoft sign-in", "Signs people in with their Microsoft account."),
            ["youtube"] = ("service", "YouTube publishing", "Publishes finished movies to a connected YouTube channel."),
            ["tiktok"] = ("service", "TikTok publishing", "Sends finished movies to a connected TikTok account."),
        };

    public static IServiceCollection AddPoHealthChecks(this IServiceCollection services)
    {
        var quick = TimeSpan.FromSeconds(10);
        services.AddHealthChecks()
            .AddCheck<StorageHealthCheck>("storage", HealthStatus.Unhealthy, timeout: quick)
            .AddCheck<MovieFilesHealthCheck>("movie-files", HealthStatus.Degraded, timeout: quick)
            .AddCheck<FfmpegHealthCheck>("ffmpeg", HealthStatus.Degraded, timeout: quick)
            .AddTypeActivatedCheck<AiProviderHealthCheck>("google-ai", HealthStatus.Degraded, [], quick, args: [new[] { ConfigKeys.GoogleApiKey }])
            .AddTypeActivatedCheck<AiProviderHealthCheck>("azure-openai", HealthStatus.Degraded, [], quick, args: [new[] { ConfigKeys.OpenAiEndpoint, ConfigKeys.OpenAiKey }])
            .AddTypeActivatedCheck<AiProviderHealthCheck>("ai-foundry", HealthStatus.Degraded, [], quick, args: [new[] { ConfigKeys.AiFoundryEndpoint, ConfigKeys.AiFoundryKey }])
            .AddCheck<KeyVaultHealthCheck>("key-vault", HealthStatus.Degraded, timeout: quick)
            .AddCheck<SignInHealthCheck>("sign-in", HealthStatus.Degraded, timeout: quick)
            .AddTypeActivatedCheck<PublishingHealthCheck>("youtube", HealthStatus.Degraded, [], quick, args: [PoRedoMedia.Api.Features.Publishing.Platform.YouTube])
            .AddTypeActivatedCheck<PublishingHealthCheck>("tiktok", HealthStatus.Degraded, [], quick, args: [PoRedoMedia.Api.Features.Publishing.Platform.TikTok]);
        return services;
    }
}

/// <summary>The storage account answers (a service-properties read). Unreachable fails <c>/health</c>.</summary>
public sealed class StorageHealthCheck(StorageClients storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!storage.IsConfigured)
            return new HealthCheckResult(context.Registration.FailureStatus, "No storage account is configured.");
        try
        {
            await storage.PingAsync(cancellationToken);
            return HealthCheckResult.Healthy("Reachable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The exception goes along for the logs; /api/diag shows its type only.
            return new HealthCheckResult(context.Registration.FailureStatus, "Not reachable.", ex);
        }
    }
}

/// <summary>Create Movie's folder on this server's disk can be written to.</summary>
public sealed class MovieFilesHealthCheck(MovieEngine engine) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var probe = Path.Combine(engine.Root, $".health-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(engine.Root);
            await File.WriteAllTextAsync(probe, "ok", cancellationToken);
            return HealthCheckResult.Healthy("Writable.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Cannot be written to, so Create Movie cannot work.", ex);
        }
        finally
        {
            try { File.Delete(probe); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>ffmpeg starts and says its version (the bundled copy, the configured one, or PATH).</summary>
public sealed class FfmpegHealthCheck(FFmpegProcess ffmpeg) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await ffmpeg.StartsAsync(cancellationToken)
            ? HealthCheckResult.Healthy("Found and starts.")
            : new HealthCheckResult(context.Registration.FailureStatus, "Not found on this server, so nothing can be rendered.");
}

/// <summary>
/// An AI provider has its settings. It is not called: every call costs money, and the functions that need it are
/// simply not offered without it. The mock providers (Test) count as present.
/// </summary>
public sealed class AiProviderHealthCheck(IConfiguration configuration, IHostEnvironment environment, string[] keys) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(MockAi.IsEnabled(configuration, environment)
            ? HealthCheckResult.Healthy("Stand-in (mock) provider in use.")
            : keys.All(k => !string.IsNullOrWhiteSpace(configuration[k]))
                ? HealthCheckResult.Healthy("Configured.")
                : new HealthCheckResult(context.Registration.FailureStatus, "Not configured, so the functions that need it are not offered."));
}

/// <summary>Whether the deployed app's secrets were read from Key Vault at startup (see <see cref="KeyVaultExtensions"/>).</summary>
public sealed class KeyVaultHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(KeyVaultExtensions.Loaded switch
        {
            true => HealthCheckResult.Healthy("Secrets were read at startup."),
            false => new HealthCheckResult(context.Registration.FailureStatus, "Could not be read at startup; running on local settings."),
            null => HealthCheckResult.Healthy("Not used here: settings come from this server's own configuration."),
        });
}

/// <summary>Microsoft (Entra) sign-in has its app registration. Without it only developer sign-in exists (Dev/Test).</summary>
public sealed class SignInHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(configuration[ConfigKeys.AzureAdClientId])
            ? HealthCheckResult.Healthy("Configured.")
            : new HealthCheckResult(context.Registration.FailureStatus, "Not configured: only developer sign-in is available."));
}

/// <summary>A publishing site has its app keys (it is not called: connecting is per user, from the Publish dialog).</summary>
public sealed class PublishingHealthCheck(PoRedoMedia.Api.Features.Publishing.IYouTubePublisher youTube, PoRedoMedia.Api.Features.Publishing.ITikTokPublisher tikTok,
    PoRedoMedia.Api.Features.Publishing.Platform platform) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult((platform == PoRedoMedia.Api.Features.Publishing.Platform.YouTube ? youTube.Configured : tikTok.Configured)
            ? HealthCheckResult.Healthy("Configured; each user connects their own account.")
            : new HealthCheckResult(context.Registration.FailureStatus, "Not configured, so Publish does not offer it (see docs/publishing-setup.md)."));
}
