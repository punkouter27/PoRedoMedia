using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Movie.Library;
using PoRedoMedia.Movie.Pictures;
using PoRedoMedia.Movie.Render;
using PoRedoMedia.Movie.Script;
using PoRedoMedia.Movie.Voice;
using SkiaSharp;

namespace PoRedoMedia.Api.Features.Movies;

/// <summary>
/// The movie engine asks for every outside service through a delegate. These are the ones this app fills them with:
/// its own chat resource for the script, its speech deployments for the voice, and its picture and video providers.
/// </summary>
public static class MovieAi
{
    public const string HttpClientName = "movie";

    /// <summary>The client a finished video is sent to YouTube with: a whole video goes in one request, so it has no time limit.</summary>
    public const string UploadClientName = "movie-upload";

    /// <summary>The chat resource's address and key: the app's OpenAI settings, else its Foundry ones; null when it has neither.</summary>
    public static (string Endpoint, string Key)? ChatResource(IConfiguration config) =>
        AiFoundryClient.Setting(config, ConfigKeys.OpenAiEndpoint) is { } endpoint && AiFoundryClient.Setting(config, ConfigKeys.OpenAiKey) is { } key ? (endpoint, key)
        : SpeechResource(config);

    /// <summary>The Foundry resource the voice and the listening run on; null when it is not configured.</summary>
    public static (string Endpoint, string Key)? SpeechResource(IConfiguration config) =>
        AiFoundryClient.Setting(config, ConfigKeys.AiFoundryEndpoint) is { } endpoint && AiFoundryClient.Setting(config, ConfigKeys.AiFoundryKey) is { } key ? (endpoint, key) : null;

    /// <summary>
    /// The two ways a script is asked for: words alone, and words with pictures to look at. A whole plan is one long
    /// reply, so the time allowed is long and a failed request is tried again once at most, not paid for four times.
    /// </summary>
    /// <param name="count">Told the model, and how many tokens went in, came back from its cache, and came out.</param>
    public static (JsonChat Chat, SeeingChat See) Chats(string endpoint, string key, Action<string, int, int, int> count)
    {
        var client = new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions
        {
            Endpoint = AiFoundryClient.AzureV1Endpoint(endpoint),
            NetworkTimeout = TimeSpan.FromMinutes(10),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 1),
        });

        static ChatCompletionOptions Shape(string schemaName, string schema) => new()
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(schemaName, BinaryData.FromString(schema), jsonSchemaIsStrict: true),
        };

        void Count(string deployment, ChatTokenUsage usage) =>
            count(deployment, usage.InputTokenCount, usage.InputTokenDetails?.CachedTokenCount ?? 0, usage.OutputTokenCount);

        // The filter in front of the model turns a request away with an ordinary "bad request", and says in the reply
        // which kind of content it objected to. A busy service is told apart too, so the writer can try its fast model.
        static async Task<string> Guarded(string deployment, Func<Task<string>> ask)
        {
            try
            {
                return await ask();
            }
            catch (ClientResultException e) when (e.Status == 400 && ScriptDeclinedException.FilterVerdict(e.GetRawResponse()?.Content.ToString() ?? "") is { } verdict)
            {
                throw new ScriptDeclinedException(DeclinedBy.RequestFilter, verdict.Length > 0 ? verdict : null, deployment);
            }
            catch (ClientResultException e) when (e.Status is 429 or 503)
            {
                throw new ServiceBusyException("The script service is busy. Wait a minute and try again.", e);
            }
            catch (ClientResultException e)
            {
                throw new InvalidOperationException($"The script service answered {e.Status}: {e.Message.Split('\n')[0].Trim()}", e);
            }
        }

        JsonChat chat = (deployment, system, user, schemaName, schema, onText, ct) => Guarded(deployment, async () =>
        {
            ChatMessage[] messages = [new SystemChatMessage(system), new UserChatMessage(user)];
            var reply = new StringBuilder();
            await foreach (var update in client.GetChatClient(deployment).CompleteChatStreamingAsync(messages, Shape(schemaName, schema), ct))
            {
                // A declined or cut-off reply is not valid JSON; say what happened instead of "could not be read".
                if (!string.IsNullOrEmpty(update.RefusalUpdate))
                    throw new ScriptDeclinedException(DeclinedBy.Model, null, deployment);
                if (update.FinishReason == ChatFinishReason.ContentFilter)
                    throw new ScriptDeclinedException(DeclinedBy.ReplyFilter, null, deployment);
                if (update.FinishReason == ChatFinishReason.Length)
                    throw new InvalidOperationException("The script was cut off before it finished. Try again with fewer films or a shorter topic.");
                if (update.Usage is { } usage)
                    Count(deployment, usage);
                foreach (var part in update.ContentUpdate)
                {
                    reply.Append(part.Text);
                    onText?.Invoke(part.Text);
                }
            }
            return reply.ToString();
        });

        SeeingChat see = (deployment, system, user, pictures, schemaName, schema, ct) => Guarded(deployment, async () =>
        {
            List<ChatMessageContentPart> parts = [ChatMessageContentPart.CreateTextPart(user)];
            foreach (var (label, path) in pictures)
            {
                parts.Add(ChatMessageContentPart.CreateTextPart(label));
                // Low detail is a fixed, small number of tokens a picture, and enough to see what happens in it.
                var type = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                parts.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(await File.ReadAllBytesAsync(path, ct)), type, ChatImageDetailLevel.Low));
            }
            var completion = (await client.GetChatClient(deployment).CompleteChatAsync([new SystemChatMessage(system), new UserChatMessage(parts)], Shape(schemaName, schema), ct)).Value;
            if (completion.Usage is { } usage)
                Count(deployment, usage);
            if (!string.IsNullOrEmpty(completion.Refusal))
                throw new ScriptDeclinedException(DeclinedBy.Model, null, deployment);
            if (completion.FinishReason == ChatFinishReason.ContentFilter)
                throw new ScriptDeclinedException(DeclinedBy.ReplyFilter, null, deployment);
            return completion.Content.Count > 0 ? completion.Content[0].Text : "";
        });

        return (chat, see);
    }

    /// <summary>
    /// Pictures from the app's own picture provider. One with a reference picture is drawn from it, so a subject keeps
    /// its look; one without is drawn in the frame's own shape. Saved as a PNG, whatever the provider sent.
    /// </summary>
    public static ImageMaker Pictures(IImageGenerationService images) => async (request, outputPath, ct) =>
    {
        GeneratedImage made;
        try
        {
            made = request.ReferencePath is { } reference
                ? await images.EditAsync(request.Prompt, ImageBytes.ForProcessing(await File.ReadAllBytesAsync(reference, ct)), 0, ct)
                : await images.GenerateAsync(request.Prompt, ShapeOf(request.Size), ct);
        }
        catch (Exception e) when (e is not (OperationCanceledException or InvalidOperationException))
        {
            throw new InvalidOperationException($"The picture could not be made. {e.Message.Split('\n')[0].Trim()}", e);
        }
        using var bitmap = ImageBytes.Decode(made.Data);
        Files.EnsureFolderFor(outputPath);
        await File.WriteAllBytesAsync(outputPath, ImageBytes.ToPng(bitmap), ct);
    };

    /// <summary>A blank picture that only lends its shape: wide for a frame of the video, square for a logo.</summary>
    private static byte[] ShapeOf(string size)
    {
        var square = size.Split('x') is [var w, var h] && w == h;
        using var blank = new SKBitmap(square ? 32 : 64, square ? 32 : 36);
        blank.Erase(SKColors.White);
        return ImageBytes.ToPng(blank);
    }

    /// <summary>
    /// A few seconds of generated footage. The video provider animates a picture, so the shot is drawn first and then
    /// set moving: one picture and one video are what a clip of this kind costs.
    /// </summary>
    public static Func<string, string, CancellationToken, Task> Footage(IImageGenerationService images, IVideoGenerationService video) => async (shot, outputPath, ct) =>
    {
        try
        {
            var still = await images.GenerateAsync(shot, ShapeOf("wide"), ct);
            var clip = await video.GenerateAsync(ImageBytes.ForProcessing(still.Data), shot, ct);
            Files.EnsureFolderFor(outputPath);
            await File.WriteAllBytesAsync(outputPath, clip, ct);
        }
        catch (Exception e) when (e is not (OperationCanceledException or InvalidOperationException))
        {
            throw new InvalidOperationException($"The video could not be made. {e.Message.Split('\n')[0].Trim()}", e);
        }
    };

    /// <summary>
    /// What is said in a recording and when, from the app's Whisper deployment. The timed words come as they were
    /// heard; where the sentence as written has as many, its spelling and punctuation are used, which reads better.
    /// </summary>
    public static Hear Listener(IHttpClientFactory http, string endpoint, string key, string deployment, Func<Action<string, int, decimal>?> used) => async (wavPath, ct) =>
    {
        await using var audio = File.OpenRead(wavPath);
        using var form = new MultipartFormDataContent
        {
            { new StreamContent(audio), "file", Path.GetFileName(wavPath) },
            { new StringContent("verbose_json"), "response_format" },
            { new StringContent("word"), "timestamp_granularities[]" },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(deployment)}/audio/transcriptions?api-version=2024-06-01")
        { Content = form };
        request.Headers.Add("api-key", key);
        using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The speech service could not listen to this ({(int)response.StatusCode}).");

        using var json = JsonDocument.Parse(body);
        var heard = new List<WordTiming>();
        if (json.RootElement.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array)
            foreach (var word in words.EnumerateArray())
            {
                var (start, end) = (word.GetProperty("start").GetDouble(), word.GetProperty("end").GetDouble());
                if (word.GetProperty("word").GetString()?.Trim() is { Length: > 0 } text)
                    heard.Add(new WordTiming(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(Math.Max(0.01, end - start))));
            }
        var written = json.RootElement.TryGetProperty("text", out var said) ? Durations.SplitWords(said.GetString() ?? "") : [];
        if (written.Length == heard.Count)
            heard = [.. heard.Select((w, i) => w with { Text = written[i] })];
        if (json.RootElement.TryGetProperty("duration", out var length) && length.ValueKind == JsonValueKind.Number)
            used()?.Invoke(SpendLog.HeardSeconds, (int)Math.Ceiling(length.GetDouble()), 0);
        return heard;
    };
}

/// <summary>
/// The narrator's voice from the app's speech deployment. The service gives the sound alone, so the recording is
/// listened back to for when each word is said; where that cannot be done, or it hears other words than were
/// written, the words are spread across the recording by their length.
/// </summary>
public sealed class SpeechNarrator(IHttpClientFactory http, FfmpegRunner ffmpeg, string endpoint, string key, string deployment, string voice, Hear? hear, decimal? ratePerMillion) : INarrator
{
    /// <summary>The API version that serves <c>audio/speech</c> with <c>instructions</c>.</summary>
    private const string ApiVersion = "2025-03-01-preview";

    private const string Delivery = "Speak as the friendly narrator of a short video: clear, warm and lively, at a natural pace.";

    /// <summary>Told what each recording used: its name in the spend log, how many characters, and their price when the rate is known.</summary>
    public Action<string, int, decimal>? Used { get; set; }

    public async Task<Narration> SynthesizeAsync(string text, string outputPath, double rate, CancellationToken ct)
    {
        if (text.Trim().Length == 0)
            throw new InvalidOperationException("This clip has no words to say. Write some, or let it keep its own sound.");

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(deployment)}/audio/speech?api-version={ApiVersion}")
        {
            Content = JsonContent.Create(new { model = deployment, input = text, voice, instructions = Delivery, response_format = "mp3" }),
        };
        request.Headers.Add("api-key", key);
        using var response = await http.CreateClient(MovieAi.HttpClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The voice service could not speak this line ({(int)response.StatusCode}). Try again in a moment.");
        var sound = await response.Content.ReadAsByteArrayAsync(ct);
        if (sound.Length < 256)
            throw new InvalidOperationException($"The voice service answered with no sound for “{(text.Length > 60 ? text[..60] + "…" : text)}”. Try again in a moment.");

        // Made in the scratch folder and moved in whole, so a recording cut short is never taken for a finished one.
        var (heardAs, wav) = (Files.NewScratchFile(".mp3"), Files.NewScratchFile(".wav"));
        try
        {
            await File.WriteAllBytesAsync(heardAs, sound, ct);
            // The pace moves off 1.0 only to fit a line to its film, by a little; the pitch is kept.
            string[] tempo = Math.Abs(rate - 1.0) < 0.005 ? [] : ["-af", FormattableString.Invariant($"atempo={Math.Clamp(rate, 0.5, 2.0):0.###}")];
            await ffmpeg.RunAsync(["-y", "-i", heardAs, .. tempo, "-ar", "48000", "-ac", "1", "-c:a", "pcm_s16le", wav], Path.GetDirectoryName(wav)!, null, null, ct);
            Files.EnsureFolderFor(outputPath);
            File.Move(wav, outputPath, overwrite: true);
        }
        finally
        {
            File.Delete(heardAs);
            File.Delete(wav);
        }

        Used?.Invoke(SpendLog.VoiceCharacters, text.Length, (ratePerMillion ?? 0) * text.Length / 1_000_000m);
        var duration = WavInfo.Duration(outputPath);
        return new Narration(outputPath, duration, await TimingsAsync(text, outputPath, duration, ct));
    }

    private async Task<IReadOnlyList<WordTiming>> TimingsAsync(string text, string wav, TimeSpan duration, CancellationToken ct)
    {
        var written = Durations.SplitWords(text);
        if (hear is not null)
            try
            {
                var heard = await hear(wav, ct);
                if (heard.Count == written.Length)
                    return [.. heard.Select((w, i) => w with { Text = written[i] })];
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Not heard: the captions are still timed, a little less exactly.
            }
        return WordTiming.Spread(text, duration);
    }
}
