using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace PoRedoMedia.Api.Common.Ai;

// Provider-level fallback: when Gemini and Azure OpenAI are both configured, a call the first
// one fails is asked once of the other. It sits above each provider's own transport retries (the
// Gemini client's resilience handler), so it only acts once those have given up.

/// <summary>Asks <paramref name="fallback"/> once when <paramref name="primary"/> fails, and notes it in the run's log.</summary>
public sealed partial class FallbackVisionService(
    IVisionService primary, string primaryName, IVisionService fallback, string fallbackName, ILogger<FallbackVisionService> logger) : IVisionService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Primary} vision failed; asking {Fallback} instead")]
    private partial void LogFellBack(Exception ex, string primary, string fallback);

    public async Task<VisionResult> AnalyzeAsync(byte[] image, CancellationToken ct = default)
    {
        try
        {
            return await primary.AnalyzeAsync(image, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogFellBack(ex, primaryName, fallbackName);
            AiMeter.Tally?.NoteFallback(ProviderFallback.Message("vision", primaryName, fallbackName));
            return await fallback.AnalyzeAsync(image, ct);
        }
    }
}

/// <summary>The chat counterpart of <see cref="FallbackVisionService"/>. Streaming is not retried: nothing here streams.</summary>
public sealed partial class FallbackChatClient(
    IChatClient primary, string primaryName, IChatClient fallback, string fallbackName, ILogger<FallbackChatClient> logger)
    : DelegatingChatClient(primary)
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Primary} chat failed; asking {Fallback} instead")]
    private partial void LogFellBack(Exception ex, string primary, string fallback);

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? [.. messages];
        try
        {
            return await base.GetResponseAsync(list, options, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogFellBack(ex, primaryName, fallbackName);
            AiMeter.Tally?.NoteFallback(ProviderFallback.Message("chat", primaryName, fallbackName));

            // A model named for the primary means nothing to the other provider.
            var moved = options?.Clone();
            if (moved is not null)
                moved.ModelId = null;
            return await fallback.GetResponseAsync(list, moved, cancellationToken);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            fallback.Dispose();
        base.Dispose(disposing);
    }
}

public static class ProviderFallback
{
    public const string Gemini = "Gemini";
    public const string AzureOpenAi = "Azure OpenAI";

    /// <summary>Google's OpenAI-compatible endpoint, which lets Gemini stand behind the same <see cref="IChatClient"/>.</summary>
    internal static readonly Uri GeminiOpenAiEndpoint = new("https://generativelanguage.googleapis.com/v1beta/openai/");

    /// <summary>The line the run's log shows.</summary>
    public static string Message(string what, string primary, string fallback) => $"{primary} {what} failed; {fallback} answered instead";

    /// <summary>Gemini as a chat client, or null when there is no Google key.</summary>
    /// <param name="options">For tests: a transport to send through. The endpoint is set here either way.</param>
    public static IChatClient? GeminiChat(IConfiguration configuration, OpenAIClientOptions? options = null)
    {
        if (configuration[ConfigKeys.GoogleApiKey] is not { Length: > 0 } key)
            return null;

        options ??= new OpenAIClientOptions();
        options.Endpoint = GeminiOpenAiEndpoint;
        var model = configuration[ConfigKeys.GoogleVisionModel] is { Length: > 0 } m ? m : GeminiVisionService.DefaultModel;
        return new OpenAIClient(new ApiKeyCredential(key), options).GetChatClient(model).AsIChatClient();
    }
}
