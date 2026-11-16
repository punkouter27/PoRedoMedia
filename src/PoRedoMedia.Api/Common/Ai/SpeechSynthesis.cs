namespace PoRedoMedia.Api.Common.Ai;

/// <summary>Speaks words in a chosen voice, told how to deliver them. <see cref="IsConfigured"/> is false with no speech model.</summary>
public interface ISpeechSynthesis
{
    bool IsConfigured { get; }

    /// <summary>The words as an MP3.</summary>
    /// <param name="voice">One of <see cref="AiFoundrySpeech.Voices"/>.</param>
    /// <param name="instructions">How to say it, such as "a panicked whisper".</param>
    Task<byte[]> SpeakAsync(string text, string voice, string instructions, CancellationToken ct = default);
}

/// <summary>gpt-4o-mini-tts on Azure AI Foundry (<c>AiFoundry:TtsDeployment</c>).</summary>
public sealed class AiFoundrySpeech(IConfiguration configuration, IHttpClientFactory http) : ISpeechSynthesis
{
    public const string HttpClientName = "speech";

    /// <summary>The voices the model has.</summary>
    public static readonly string[] Voices = ["alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer", "verse"];

    /// <summary>The API version that serves <c>audio/speech</c> for gpt-4o-mini-tts with <c>instructions</c>.</summary>
    private const string SpeechApiVersion = "2025-03-01-preview";

    /// <summary>The speech API's input limit.</summary>
    private const int MaxInput = 4096;

    public bool IsConfigured => IsAvailable(configuration);

    public static bool IsAvailable(IConfiguration configuration) =>
        new[] { ConfigKeys.AiFoundryEndpoint, ConfigKeys.AiFoundryKey, ConfigKeys.AiFoundryTtsDeployment }
            .All(k => !string.IsNullOrWhiteSpace(configuration[k]));

    public async Task<byte[]> SpeakAsync(string text, string voice, string instructions, CancellationToken ct = default)
    {
        var endpoint = configuration[ConfigKeys.AiFoundryEndpoint]!.TrimEnd('/');
        var deployment = configuration[ConfigKeys.AiFoundryTtsDeployment]!;
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{endpoint}/openai/deployments/{Uri.EscapeDataString(deployment)}/audio/speech?api-version={SpeechApiVersion}")
        {
            Content = JsonContent.Create(new
            {
                model = deployment,
                input = text.Length <= MaxInput ? text : text[..MaxInput],
                voice = Voices.Contains(voice) ? voice : "ash",
                instructions,
                response_format = "mp3",
            }),
        };
        request.Headers.Add("api-key", configuration[ConfigKeys.AiFoundryKey]);

        using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Speech returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}

/// <summary>No speech model (mock runs, or none configured): callers time the words and stay silent.</summary>
public sealed class NoSpeech : ISpeechSynthesis
{
    public bool IsConfigured => false;

    public Task<byte[]> SpeakAsync(string text, string voice, string instructions, CancellationToken ct = default) =>
        throw new InvalidOperationException("No speech model is configured.");
}
