using System.Net;
using System.Net.Http.Headers;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoRedoMedia.Api.Configuration;

namespace PoRedoMedia.Api.Common.Health;

/// <summary>
/// Live probes of the outside services for <c>/diag</c> and <c>/health</c>. Each asks the service something that costs
/// nothing (a model list, a metadata document, one page of secret names) and turns the answer into a fixed sentence:
/// no address, key, status text or exception message ever goes out. A service that is not set up is not called.
/// </summary>
internal static class ServiceProbe
{
    /// <summary>The named HttpClient every probe uses (short timeout, no retries).</summary>
    public const string HttpClientName = "diag-probe";

    public static IServiceCollection AddServiceProbes(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(8));
        return services;
    }

    /// <summary>Sends one GET and says, in plain words, whether the service answered and accepted the key.</summary>
    public static async Task<HealthCheckResult> GetAsync(IHttpClientFactory http, HealthCheckContext context, Uri url,
        Action<HttpRequestHeaders>? headers, bool anyAnswerIsHealthy, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            headers?.Invoke(request.Headers);
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode || anyAnswerIsHealthy)
                return HealthCheckResult.Healthy(anyAnswerIsHealthy ? "Reachable." : "Answers, and accepts this app's key.");
            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? new HealthCheckResult(context.Registration.FailureStatus, "Answers, but refuses this app's key, so the functions that need it fail.")
                : new HealthCheckResult(context.Registration.FailureStatus, $"Answered with an error ({(int)response.StatusCode}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Not reachable from this server.", ex);
        }
    }

    public static string? Setting(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value) ? value : null;
}

/// <summary>Google AI (Gemini): reads the vision model's description with the app's key.</summary>
public sealed class GoogleAiHealthCheck(IConfiguration configuration, IHostEnvironment environment, IHttpClientFactory http) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (MockAi.IsEnabled(configuration, environment))
            return HealthCheckResult.Healthy("Stand-in (mock) provider in use.");
        if (ServiceProbe.Setting(configuration, ConfigKeys.GoogleApiKey) is not { } key)
            return new HealthCheckResult(context.Registration.FailureStatus, "Not configured, so the functions that need it are not offered.");

        var model = ServiceProbe.Setting(configuration, ConfigKeys.GoogleVisionModel) ?? "gemini-flash-lite-latest";
        var url = new Uri($"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}");
        return await ServiceProbe.GetAsync(http, context, url, h => h.Add("x-goog-api-key", key), anyAnswerIsHealthy: false, cancellationToken);
    }
}

/// <summary>An Azure OpenAI / AI Foundry resource: lists its models (free) with the app's key.</summary>
public sealed class AzureAiResourceHealthCheck(IConfiguration configuration, IHostEnvironment environment, IHttpClientFactory http,
    string endpointKey, string keyKey) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (MockAi.IsEnabled(configuration, environment))
            return HealthCheckResult.Healthy("Stand-in (mock) provider in use.");
        if (ServiceProbe.Setting(configuration, endpointKey) is not { } endpoint || ServiceProbe.Setting(configuration, keyKey) is not { } key)
            return new HealthCheckResult(context.Registration.FailureStatus, "Not configured, so the functions that need it are not offered.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            return new HealthCheckResult(context.Registration.FailureStatus, "Its address setting is not a valid address.");

        var url = new Uri(AiFoundryClient.AzureV1Endpoint(endpoint), "models");
        return await ServiceProbe.GetAsync(http, context, url, h => h.Add("api-key", key), anyAnswerIsHealthy: false, cancellationToken);
    }
}

/// <summary>
/// Key Vault: when the secrets were read at startup, reads one page of secret names now with the same identity, so a
/// lost role or a vault that stopped answering shows here.
/// </summary>
public sealed class KeyVaultHealthCheck(IConfiguration configuration, IHostEnvironment environment) : IHealthCheck
{
    private static SecretClient? _client;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        switch (KeyVaultExtensions.Loaded)
        {
            case null:
                return HealthCheckResult.Healthy("Not used here: settings come from this server's own configuration.");
            case false:
                return new HealthCheckResult(context.Registration.FailureStatus, "Could not be read at startup; running on local settings.");
        }

        if (ServiceProbe.Setting(configuration, ConfigKeys.KeyVaultUri) is not { } uri)
            return HealthCheckResult.Healthy("Secrets were read at startup.");
        try
        {
            _client ??= new SecretClient(new Uri(uri), new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ExcludeManagedIdentityCredential = environment.IsDevelopment(),
            }));
            await foreach (var _ in _client.GetPropertiesOfSecretsAsync(cancellationToken).AsPages(pageSizeHint: 1).WithCancellation(cancellationToken))
                break;
            return HealthCheckResult.Healthy("Answers; this app's identity can read its secrets.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Not reachable now, or this app's identity lost access (secrets read at startup still apply).", ex);
        }
    }
}

/// <summary>Microsoft (Entra) sign-in: the tenant's sign-in metadata answers. Without an app registration only developer sign-in exists.</summary>
public sealed class SignInHealthCheck(IConfiguration configuration, IHttpClientFactory http) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (ServiceProbe.Setting(configuration, ConfigKeys.AzureAdClientId) is null)
            return new HealthCheckResult(context.Registration.FailureStatus, "Not configured: only developer sign-in is available.");

        var tenant = ServiceProbe.Setting(configuration, ConfigKeys.AzureAdTenantId) ?? "common";
        var url = new Uri($"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/v2.0/.well-known/openid-configuration");
        var result = await ServiceProbe.GetAsync(http, context, url, null, anyAnswerIsHealthy: false, cancellationToken);
        return result.Status == HealthStatus.Healthy ? HealthCheckResult.Healthy("Answers; people can sign in.") : result;
    }
}

/// <summary>Application Insights: the ingestion endpoint of the configured connection string answers. Optional.</summary>
public sealed class AppInsightsHealthCheck(IConfiguration configuration, IHostEnvironment environment, IHttpClientFactory http) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connection = ServiceProbe.Setting(configuration, ConfigKeys.ApplicationInsightsConnectionString)
            ?? ServiceProbe.Setting(configuration, "APPLICATIONINSIGHTS_CONNECTION_STRING");
        if (connection is null || environment.IsEnvironment(PoEnvironments.Test))
            return HealthCheckResult.Healthy("Not set up here, so not called: errors go to the server log only.");

        var ingestion = connection.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .FirstOrDefault(kv => kv.Length == 2 && kv[0].Trim().Equals("IngestionEndpoint", StringComparison.OrdinalIgnoreCase))?[1].Trim()
            ?? "https://dc.services.visualstudio.com/";
        if (!Uri.TryCreate(ingestion, UriKind.Absolute, out var url))
            return new HealthCheckResult(context.Registration.FailureStatus, "Its connection setting is not valid.");
        var result = await ServiceProbe.GetAsync(http, context, url, null, anyAnswerIsHealthy: true, cancellationToken);
        return result.Status == HealthStatus.Healthy ? HealthCheckResult.Healthy("Reachable; telemetry is sent.") : result;
    }
}

/// <summary>Pexels stock photos for Create Movie: one curated photo is listed with the app's key. Optional.</summary>
public sealed class PexelsHealthCheck(IConfiguration configuration, IHostEnvironment environment, IHttpClientFactory http) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (MockAi.IsEnabled(configuration, environment))
            return HealthCheckResult.Healthy("Not called: stand-in (mock) providers are in use.");
        if (ServiceProbe.Setting(configuration, Features.Movies.MovieKeys.PexelsKey) is not { } key)
            return HealthCheckResult.Healthy("Not set up here, so not called: Create Movie draws its pictures instead.");

        return await ServiceProbe.GetAsync(http, context, new Uri("https://api.pexels.com/v1/curated?per_page=1"),
            h => h.TryAddWithoutValidation("Authorization", key), anyAnswerIsHealthy: false, cancellationToken);
    }
}
