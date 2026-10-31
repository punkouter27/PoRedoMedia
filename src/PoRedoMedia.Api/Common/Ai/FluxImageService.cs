using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>
/// FLUX.1 Kontext on Azure AI Foundry: the stand-in image model when Gemini cannot draw (out of
/// credit, down, or refusing). It edits the picture it is given, so a redraw keeps the photo's shape.
/// </summary>
public sealed partial class FluxImageService(IConfiguration configuration, IHttpClientFactory http, ILogger<FluxImageService> logger)
    : IImageGenerationService
{
    public const string HttpClientName = "flux";
    private const string ApiVersion = "2025-04-01-preview";

    /// <summary>Longest prompt sent. The model reads a short instruction best; the tail (the style) is what matters most.</summary>
    internal const int MaxPrompt = 1000;

    // ponytail: the deployment has capacity 1 (about one picture a minute), so pictures queue here
    // in this process and a 429 waits for the time the service names.
    private static readonly SemaphoreSlim Drawing = new(1);

    [LoggerMessage(Level = LogLevel.Information, Message = "FLUX is rate limited; waiting {Seconds}s")]
    private partial void LogWaiting(int seconds);

    private string Deployment => configuration[ConfigKeys.AiFoundryImageDeployment]!;

    /// <summary>Configured when Foundry has an endpoint, a key and an image deployment.</summary>
    public static bool IsConfigured(IConfiguration configuration) =>
        new[] { ConfigKeys.AiFoundryEndpoint, ConfigKeys.AiFoundryKey, ConfigKeys.AiFoundryImageDeployment }
            .All(k => !string.IsNullOrWhiteSpace(configuration[k]));

    public Task<GeneratedImage> GenerateAsync(string prompt, byte[]? matchAspectOf = null, CancellationToken ct = default) =>
        matchAspectOf is null
            ? SendAsync("images/generations", JsonContent.Create(new { prompt = Fit(prompt), model = Deployment, size = "1024x1024", output_format = "png" }), ct)
            : EditAsync("Redraw this picture as described. " + prompt, matchAspectOf, 0, ct);

    public Task<GeneratedImage> EditAsync(string prompt, byte[] image, int seed = 0, CancellationToken ct = default)
    {
        // This API takes no seed; a numbered take in the words still makes the drawings differ.
        var form = new MultipartFormDataContent
        {
            { new StringContent(Fit(seed == 0 ? prompt : $"{prompt} (take {seed + 1})")), "prompt" },
            { new StringContent(Deployment), "model" },
        };
        var png = image is [0x89, (byte)'P', (byte)'N', (byte)'G', ..];
        var picture = new ByteArrayContent(image);
        picture.Headers.ContentType = new MediaTypeHeaderValue(png ? "image/png" : "image/jpeg");
        form.Add(picture, "image", png ? "image.png" : "image.jpg");
        return SendAsync("images/edits", form, ct);
    }

    /// <summary>Keeps the last paragraph (the style) whole and trims the description before it.</summary>
    internal static string Fit(string prompt)
    {
        if (prompt.Length <= MaxPrompt)
            return prompt;
        var split = prompt.LastIndexOf("\n\n", StringComparison.Ordinal);
        var tail = split < 0 || prompt.Length - split > MaxPrompt / 2 ? "" : prompt[split..];
        return prompt[..(MaxPrompt - tail.Length)] + tail;
    }

    private async Task<GeneratedImage> SendAsync(string path, HttpContent content, CancellationToken ct)
    {
        var endpoint = configuration[ConfigKeys.AiFoundryEndpoint]!.TrimEnd('/');
        var url = $"{endpoint}/openai/deployments/{Uri.EscapeDataString(Deployment)}/{path}?api-version={ApiVersion}";
        var body = await content.ReadAsByteArrayAsync(ct);
        var type = content.Headers.ContentType;
        content.Dispose();

        await Drawing.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
                request.Content.Headers.ContentType = type;
                request.Headers.Add("api-key", configuration[ConfigKeys.AiFoundryKey]);
                using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
                var text = await response.Content.ReadAsStringAsync(ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
                {
                    var seconds = Math.Clamp(RetryAfter(response, text), 5, 65);
                    LogWaiting(seconds);
                    await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"FLUX image returned {(int)response.StatusCode}: {text}");

                using var json = JsonDocument.Parse(text);
                var data = json.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString()
                    ?? throw new RunStepException("The stand-in image model returned no picture. Try again.");
                return new GeneratedImage(Convert.FromBase64String(data), "image/png");
            }
        }
        finally
        {
            Drawing.Release();
        }
    }

    private static int RetryAfter(HttpResponseMessage response, string body) =>
        response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds)
        : RetryAfterText().Match(body) is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
        : 60;

    [GeneratedRegex(@"retry after (\d+) seconds", RegexOptions.IgnoreCase)]
    private static partial Regex RetryAfterText();
}

/// <summary>Draws with <paramref name="primary"/>; when that fails, draws once with <paramref name="fallback"/> and notes it in the run's log.</summary>
public sealed partial class FallbackImageService(
    IImageGenerationService primary, string primaryName, IImageGenerationService fallback, string fallbackName, ILogger<FallbackImageService> logger)
    : IImageGenerationService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Primary} image failed; asking {Fallback} instead")]
    private partial void LogFellBack(Exception ex, string primary, string fallback);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Fallback} image failed too")]
    private partial void LogBothFailed(Exception ex, string fallback);

    public Task<GeneratedImage> GenerateAsync(string prompt, byte[]? matchAspectOf = null, CancellationToken ct = default) =>
        TryAsync(p => p.GenerateAsync(prompt, matchAspectOf, ct), ct);

    public Task<GeneratedImage> EditAsync(string prompt, byte[] image, int seed = 0, CancellationToken ct = default) =>
        TryAsync(p => p.EditAsync(prompt, image, seed, ct), ct);

    private async Task<GeneratedImage> TryAsync(Func<IImageGenerationService, Task<GeneratedImage>> draw, CancellationToken ct)
    {
        try
        {
            return await draw(primary);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogFellBack(ex, primaryName, fallbackName);
            try
            {
                var drawn = await draw(fallback);
                AiMeter.Tally?.NoteFallback(ProviderFallback.Message("image", primaryName, fallbackName));
                return drawn;
            }
            catch (Exception second) when (!ct.IsCancellationRequested)
            {
                // The first failure says what the user can fix (such as credit); keep it.
                LogBothFailed(second, fallbackName);
                ExceptionDispatchInfo.Throw(ex);
                throw;
            }
        }
    }
}
