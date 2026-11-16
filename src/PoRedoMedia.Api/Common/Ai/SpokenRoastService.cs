using System.Diagnostics;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>
/// The stand-in performer when Lyria cannot perform (out of credit, down): gpt-4o-mini-tts on Azure
/// AI Foundry speaks the set, told by the style prompt how to deliver it. No crowd or music, just the voice.
/// </summary>
public sealed class SpokenRoastService(IConfiguration configuration, IHttpClientFactory http) : IMusicGenerationService
{
    private readonly AiFoundrySpeech _speech = new(configuration, http);

    public bool IsConfigured => _speech.IsConfigured;

    public static bool IsAvailable(IConfiguration configuration) => AiFoundrySpeech.IsAvailable(configuration);

    public async Task<MusicGenerationResult> GenerateAsync(string lyrics, string stylePrompt, CancellationToken ct = default)
    {
        var start = Stopwatch.GetTimestamp();
        var audio = await _speech.SpeakAsync(lyrics, "ash", stylePrompt, ct);
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
