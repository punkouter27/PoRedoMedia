using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Api.Features.BulkStyles;
using PoRedoMedia.Api.Features.MemeCaption;
using PoRedoMedia.Api.Features.Video;
using PoRedoMedia.Shared;
using PoRedoMedia.Shared.Models;
using SkiaSharp;

namespace PoRedoMedia.UnitTests;

public sealed class ImageAiTests
{
    private static readonly byte[] Image = [1, 2, 3];

    private sealed class StubChat(string reply) : IChatClient
    {
        public List<ChatMessage> Seen { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Seen.AddRange(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>Answers each call with the next reply, then repeats the last.</summary>
    private sealed class ScriptedChat(params string[] replies) : IChatClient
    {
        private int _next;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, replies[Math.Min(_next++, replies.Length - 1)])));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class CountingVision(string description) : IVisionService
    {
        public int Calls { get; private set; }

        public Task<VisionResult> AnalyzeAsync(byte[] image, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new VisionResult(description, [], 1));
        }
    }

    /// <summary>A provider that is down: every call fails, after honouring cancellation.</summary>
    private sealed class DownProvider : IVisionService, IChatClient
    {
        public Task<VisionResult> AnalyzeAsync(byte[] image, CancellationToken ct = default) => Fail<VisionResult>(ct);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Fail<ChatResponse>(cancellationToken);

        private static Task<T> Fail<T>(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            throw new HttpRequestException("503 Service Unavailable");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public async Task A_failed_vision_or_chat_call_is_asked_once_of_the_other_provider_and_the_run_log_says_so()
    {
        var tally = AiMeter.Begin();
        var vision = new FallbackVisionService(
            new DownProvider(), ProviderFallback.Gemini, new CountingVision("seen by Azure"), ProviderFallback.AzureOpenAi, NullLogger<FallbackVisionService>.Instance);
        using var chat = new FallbackChatClient(
            new DownProvider(), ProviderFallback.AzureOpenAi, new StubChat("from Gemini"), ProviderFallback.Gemini, NullLogger<FallbackChatClient>.Instance);
        using var healthy = new FallbackChatClient(
            new StubChat("from Azure"), ProviderFallback.AzureOpenAi, new DownProvider(), ProviderFallback.Gemini, NullLogger<FallbackChatClient>.Instance);

        Assert.Equal("seen by Azure", (await vision.AnalyzeAsync(Image)).Description);
        Assert.Equal("from Gemini", (await chat.GetResponseAsync("hi", new ChatOptions { ModelId = "gpt-5.4-nano" })).Text);
        Assert.Equal("from Azure", (await healthy.GetResponseAsync("hi")).Text);
        Assert.Equal(
            ["Gemini vision failed; Azure OpenAI answered instead", "Azure OpenAI chat failed; Gemini answered instead"], tally.TakeFallbacks());
        Assert.Empty(tally.TakeFallbacks());

        // A cancelled call is the caller's choice, not a provider failure: nothing else is asked.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vision.AnalyzeAsync(Image, cancelled.Token));
        Assert.Empty(tally.TakeFallbacks());
    }

    [Fact]
    public async Task FLUX_draws_what_Gemini_fails_and_when_both_fail_the_users_first_reason_is_kept()
    {
        var tally = AiMeter.Begin();
        var outOfCredit = new RunStepException("The Google AI account is out of credit.");
        var stands = new FallbackImageService(
            new StubImages(outOfCredit), ProviderFallback.Gemini, new StubImages(null), "FLUX (Azure)", NullLogger<FallbackImageService>.Instance);
        var neither = new FallbackImageService(
            new StubImages(outOfCredit), ProviderFallback.Gemini, new StubImages(new HttpRequestException("429")), "FLUX (Azure)", NullLogger<FallbackImageService>.Instance);

        Assert.Equal([1, 2, 3], (await stands.EditAsync("watercolor", Image)).Data);
        Assert.Equal(["Gemini image failed; FLUX (Azure) answered instead"], tally.TakeFallbacks());
        Assert.Same(outOfCredit, await Assert.ThrowsAsync<RunStepException>(() => neither.GenerateAsync("a cat")));

        // The roast's performer works the same way: Azure speech performs what Lyria fails.
        var performers = new FallbackMusicService(
            new StubMusic(outOfCredit), "Lyria", new StubMusic(null), "Azure speech", NullLogger<FallbackMusicService>.Instance);
        Assert.Equal("audio/mpeg", (await performers.GenerateAsync("bars", "stand-up")).ContentType);
        Assert.Equal(["Lyria performance failed; Azure speech answered instead"], tally.TakeFallbacks());

        // A long reproduction prompt is cut from the description, never from the style at its end.
        var fitted = FluxImageService.Fit(new string('d', 3000) + "\n\nRender the whole scene in this style: watercolor");
        Assert.Equal(FluxImageService.MaxPrompt, fitted.Length);
        Assert.EndsWith("style: watercolor", fitted);
    }

    private sealed class StubMusic(Exception? failure) : IMusicGenerationService
    {
        public bool IsConfigured => true;

        public Task<MusicGenerationResult> GenerateAsync(string lyrics, string stylePrompt, CancellationToken ct = default) =>
            failure is null ? Task.FromResult(new MusicGenerationResult([1], "audio/mpeg", 1)) : Task.FromException<MusicGenerationResult>(failure);
    }

    private sealed class StubImages(Exception? failure) : IImageGenerationService
    {
        public Task<GeneratedImage> GenerateAsync(string prompt, byte[]? matchAspectOf = null, CancellationToken ct = default) => Draw();

        public Task<GeneratedImage> EditAsync(string prompt, byte[] image, int seed = 0, CancellationToken ct = default) => Draw();

        private Task<GeneratedImage> Draw() =>
            failure is null ? Task.FromResult(new GeneratedImage([1, 2, 3], "image/png")) : Task.FromException<GeneratedImage>(failure);
    }

    [Fact]
    public async Task A_weak_roast_is_rewritten_once_with_the_critique_and_a_strong_or_unjudged_one_is_kept()
    {
        var rewrites = 0;
        Task<string?> Rewrite(RoastCritique critique, CancellationToken ct)
        {
            rewrites++;
            return Task.FromResult<string?>($"sharper, after: {critique.Critique}");
        }

        Task<(string Text, RoastCritique? Critique, bool Rewritten)> Improve(IRoastCritic? critic, Func<RoastCritique, CancellationToken, Task<string?>>? rewrite = null) =>
            RoastQuality.ImproveOnceAsync(critic, "A roast", "draft", rewrite ?? Rewrite, NullLogger.Instance, default);
        ChatRoastCritic Critic(string reply) => new(new StubChat(reply), NullLogger<ChatRoastCritic>.Instance);

        var weak = await Improve(Critic("""{"score":3,"critique":"Line 2 only describes the room."}"""));
        Assert.Equal(("sharper, after: Line 2 only describes the room.", 3.0, true), (weak.Text, weak.Critique!.Score, weak.Rewritten));
        var strong = await Improve(Critic("""{"score":8,"critique":"Lands."}"""));
        Assert.Equal(("draft", false), (strong.Text, strong.Rewritten));

        // No verdict, no critic, or a rewrite that fails or comes back empty: the draft stands.
        Assert.Equal("draft", (await Improve(Critic("I refuse."))).Text);
        Assert.Equal("draft", (await Improve(null)).Text);
        Assert.Equal("draft", (await Improve(Critic("""{"score":2,"critique":"x"}"""), (_, _) => throw new HttpRequestException("down"))).Text);
        Assert.Equal("draft", (await Improve(Critic("""{"score":2,"critique":"x"}"""), (_, _) => Task.FromResult<string?>(" "))).Text);
        Assert.Equal(1, rewrites);

        // The mock critic passes every draft, so a mock run never rewrites.
        Assert.True((await new MockRoastCritic().CritiqueAsync("A roast", "draft"))!.Score >= RoastQuality.PassScore);
    }

    [Fact]
    public async Task A_result_is_named_in_at_most_six_words_from_what_the_step_kept_and_a_useless_answer_names_nothing()
    {
        var blobs = new BlobStorageService(new StorageClients(new ConfigurationBuilder().Build()));
        var roast = new MediaItem
        {
            Owner = new UserId("dev|a"), Id = MediaId.New(), Kind = PoRedoMedia.Shared.Enums.MediaKind.Audio, Status = MediaStatus.Ready,
            Origin = "RapRoast", Title = "Roast · dog", ContentType = "audio/mpeg", Extension = ".mp3", CreatedAt = DateTimeOffset.UtcNow,
            Text = "[Verse] You sat on the remote", Detail = "A dog on a sofa",
        };
        var chat = new StubChat("""{"title":"\"The Dog Who Sat On The Remote Again.\"","summary":" A stand-up roast of a dog hogging the sofa. "}""");

        var named = await new ChatMediaSummarizer(chat, blobs).SummarizeAsync(roast);

        Assert.Equal(new MediaSummary("The Dog Who Sat On The", "A stand-up roast of a dog hogging the sofa."), named);
        Assert.Contains(chat.Seen, m => m.Text.Contains("Its words: [Verse] You sat on the remote") && m.Text.Contains("What the AI saw: A dog on a sofa"));
        Assert.Null(await new ChatMediaSummarizer(new StubChat("""{"title":" ","summary":"Something."}"""), blobs).SummarizeAsync(roast));
    }

    [Theory]
    [InlineData("""{"description":" A dog on a skateboard. ","tags":["Dog","skateboard","dog",""]}""", "A dog on a skateboard.", "dog,skateboard")]
    [InlineData("A dog on a skateboard.", "A dog on a skateboard.", "")]
    public void A_vision_answer_is_read_as_json_or_kept_as_prose(string answer, string description, string tags)
    {
        var result = VisionJson.Parse(answer);

        Assert.Equal((description, tags), (result.Description, string.Join(',', result.Tags)));
    }

    [Fact]
    public void Gemini_answers_are_read_text_usage_and_whole_clip_moments_alike()
    {
        using var answered = JsonDocument.Parse("""{"candidates":[{"content":{"parts":[{"text":"hello"}]}}]}""");
        using var blocked = JsonDocument.Parse("""{"promptFeedback":{"blockReason":"SAFETY"}}""");

        Assert.Equal("hello", GeminiVisionService.Text(answered.RootElement));
        Assert.Equal("", GeminiVisionService.Text(blocked.RootElement));

        // What a call cost is read off the answer and the address it went to.
        Assert.Equal("gemini-flash-lite-latest", AiMeterHandler.ModelOf("/v1beta/models/gemini-flash-lite-latest:generateContent"));
        Assert.Equal((120, 20, 35), AiMeterHandler.GeminiUsage(Encoding.UTF8.GetBytes(
            """{"usageMetadata":{"promptTokenCount":120,"cachedContentTokenCount":20,"candidatesTokenCount":30,"thoughtsTokenCount":5}}""")));
        Assert.Equal((0, 0, 0), AiMeterHandler.GeminiUsage(Encoding.UTF8.GetBytes("not json")));

        // Whole-clip moments stay inside the clip, in time order, with tags only from the vocabulary.
        var labels = GeminiClipVisionService.Parse(
            """
            {"moments":[
              {"seconds":7.5,"label":"crowd gasps","energy":1.4,"tags":["shock","made-up"],"subjectX":0.5,"subjectY":null},
              {"seconds":1.25,"label":"man slips on ice","energy":0.9,"tags":["fail"]},
              {"seconds":12,"label":"after the end","energy":0.5,"tags":[]},
              {"seconds":3,"label":"  ","energy":0.5,"tags":[]}
            ]}
            """,
            new HashSet<string>(["fail", "shock"], StringComparer.OrdinalIgnoreCase), durationSeconds: 10);
        Assert.Equal(["man slips on ice", "crowd gasps"], labels.Select(l => l.Label));
        Assert.Equal([1.25, 7.5], labels.Select(l => l.TimestampSeconds));
        Assert.Equal((1.0, "shock", 0.5, (double?)null), (labels[1].Energy, Assert.Single(labels[1].Tags), labels[1].SubjectX, labels[1].SubjectY));
    }

    [Fact]
    public async Task A_chat_model_is_shown_the_picture_and_an_empty_answer_is_a_readable_failure()
    {
        var chat = new StubChat("""{"description":"A cat in a box","tags":["cat"]}""");

        var result = await new ChatVisionService(chat).AnalyzeAsync(Image);

        Assert.Equal("A cat in a box", result.Description);
        Assert.Contains(chat.Seen.SelectMany(m => m.Contents).OfType<DataContent>(), c => c.Data.ToArray().SequenceEqual(Image));
        await Assert.ThrowsAsync<RunStepException>(() => new ChatVisionService(new StubChat("{}")).AnalyzeAsync(Image));
    }

    [Fact]
    public async Task The_same_picture_is_analysed_once_per_provider()
    {
        var cache = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>();
        var inner = new CountingVision("seen");
        var gemini = new CachingVisionService(inner, cache, "gemini");
        var other = new CachingVisionService(inner, cache, "cv");

        await gemini.AnalyzeAsync(Image);
        await gemini.AnalyzeAsync(Image);
        await gemini.AnalyzeAsync([9, 9]);
        await other.AnalyzeAsync(Image);

        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task The_caption_writer_sends_the_description_refuses_an_empty_caption_and_keeps_the_judges_pick()
    {
        var chat = new StubChat("""{"top":"When the box arrives","bottom":"And the cat approves"}""");

        var caption = await new ChatCaptionWriter(chat).WriteAsync("A cat in a box", ["cat", "box"]);

        Assert.Equal(new MemeCaptionText("When the box arrives", "And the cat approves"), caption);
        Assert.Contains(chat.Seen, m => m.Role == ChatRole.User && m.Text.Contains("A cat in a box") && m.Text.Contains("cat, box"));
        await Assert.ThrowsAsync<RunStepException>(() => new ChatCaptionWriter(new StubChat("""{"top":"","bottom":""}""")).WriteAsync("x", []));

        // With a judge, several drafts are written and the best scored one is kept.
        var drafts = new ScriptedChat(
            """{"captions":[{"top":"a","bottom":"1"},{"top":"b","bottom":"2"},{"top":"","bottom":""}]}""",
            """{"scores":[{"draft":0,"score":3},{"draft":1,"score":9}]}""");
        Assert.Equal(new MemeCaptionText("b", "2"), await new ChatCaptionWriter(drafts, new AiJudge(drafts, NullLogger<AiJudge>.Instance)).WriteAsync("x", []));

        // A judge that gives no scores keeps the first draft rather than failing the caption.
        var broken = new ScriptedChat("""{"captions":[{"top":"a","bottom":"1"},{"top":"b","bottom":"2"}]}""", "I refuse.");
        Assert.Equal(new MemeCaptionText("a", "1"), await new ChatCaptionWriter(broken, new AiJudge(broken, NullLogger<AiJudge>.Instance)).WriteAsync("x", []));
    }

    [Fact]
    public void Bulk_styles_draws_only_the_filled_boxes_and_falls_back_only_when_every_box_is_empty()
    {
        var sent = new Dictionary<string, string>
        {
            [RunOptions.BulkPrompt(0)] = "neon {subject}",
            [RunOptions.BulkPrompt(1)] = "   ",
            [RunOptions.BulkPrompt(2)] = "",
            [RunOptions.BulkPrompt(3)] = "clay {subject}",
        };

        Assert.Equal(["neon {subject}", "clay {subject}"], BulkStylesStep.Prompts(sent, ["saved"]));
        Assert.Equal(["saved"], BulkStylesStep.Prompts(new Dictionary<string, string> { [RunOptions.BulkPrompt(0)] = " " }, ["saved"]));
    }
}
