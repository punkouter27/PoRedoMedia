using System.Diagnostics;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>
/// The stand-in performer when Lyria cannot perform (out of credit, down): gpt-4o-mini-tts on Azure
/// AI Foundry speaks the set, told by the style prompt how to deliver it. No crowd or music, just the voice.
/// </summary>
public sealed class SpokenRoastService(IConfiguration configuration, IHttpClientFactory http) : IMusicGenerationService
{
    public const string HttpClientName = "spoken-roast";

    /// <summary>The API version that serves <c>audio/speech</c> for gpt-4o-mini-tts with <c>instructions</c>.</summary>
    private const string SpeechApiVersion = "2025-03-01-preview";

    /// <summary>The speech API's input limit.</summary>
    private const int MaxInput = 4096;

    public bool IsConfigured => IsAvailable(configuration);

    public static bool IsAvailable(IConfiguration configuration) =>
        new[] { ConfigKeys.AiFoundryEndpoint, ConfigKeys.AiFoundryKey, ConfigKeys.AiFoundryTtsDeployment }
            .All(k => !string.IsNullOrWhiteSpace(configuration[k]));

    public async Task<MusicGenerationResult> GenerateAsync(string lyrics, string stylePrompt, CancellationToken ct = default)
    {
        var start = Stopwatch.GetTimestamp();
        var endpoint = configuration[ConfigKeys.AiFoundryEndpoint]!.TrimEnd('/');
        var deployment = configuration[ConfigKeys.AiFoundryTtsDeployment]!;
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{endpoint}/openai/deployments/{Uri.EscapeDataString(deployment)}/audio/speech?api-version={SpeechApiVersion}")
        {
            Content = JsonContent.Create(new
            {
                model = deployment,
                input = lyrics.Length <= MaxInput ? lyrics : lyrics[..MaxInput],
                voice = "ash",
                instructions = stylePrompt,
                response_format = "mp3",
            }),
        };
        request.Headers.Add("api-key", configuration[ConfigKeys.AiFoundryKey]);

        using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Spoken roast returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");

        var audio = await response.Content.ReadAsByteArrayAsync(ct);
        return new MusicGenerationResult(audio, "audio/mpeg", (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
}

/// <summary>Performs with <paramref name="primary"/>; when that fails (not declines), once with <paramref name="fallback"/>, noted in the run's log.</summary>
public sealed partial class FallbackMusicService(
    IMusicGenerationService primary, string primaryName, IMusicGenerationService fallback, string fallbackName, ILogger<FallbackMusicService> logger)
    : IMusicGenerationService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Primary} performance failed; asking {Fallback} instead")]
    private partial void LogFellBack(Exception ex, string primary, string fallback);

    public bool IsConfigured => primary.IsConfigured || fallback.IsConfigured;

    public async Task<MusicGenerationResult> GenerateAsync(string lyrics, string stylePrompt, CancellationToken ct = default)
    {
        try
        {
            return await primary.GenerateAsync(lyrics, stylePrompt, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogFellBack(ex, primaryName, fallbackName);
            var performed = await fallback.GenerateAsync(lyrics, stylePrompt, ct);
            AiMeter.Tally?.NoteFallback(ProviderFallback.Message("performance", primaryName, fallbackName));
            return performed;
        }
    }
}
