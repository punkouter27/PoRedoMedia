using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Api.Features.BulkStyles;
using PoRedoMedia.Api.Features.Video;
using PoRedoMedia.Api.Features.MemeCaption;
using PoRedoMedia.Api.Features.PhotoToVideo;
using PoRedoMedia.Api.Features.RapRoast;
using PoRedoMedia.Api.Features.Render;
using PoRedoMedia.Api.Features.Restyle;

namespace PoRedoMedia.Api.Configuration;

public static class FunctionRegistration
{
    /// <summary>
    /// Registers the functions that can run in this configuration. A function is registered only
    /// when everything it needs is present, so one that is not registered is simply not offered.
    /// </summary>
    public static IServiceCollection AddFunctions(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton<AiMeter>();
        services.AddSingleton<AiReadStore>();
        services.AddTransient<AiMeterHandler>();
        services.AddSingleton<MemeTemplateService>();
        services.AddSingleton<BulkPromptRepository>();
        services.AddHybridCache();
        // Google calls can be slow and occasionally fail; retry them, but never retry a timeout.
        services.AddHttpClient(GeminiVisionService.HttpClientName).AddStandardResilienceHandler(o =>
        {
            o.AttemptTimeout.Timeout = TimeSpan.FromMinutes(2);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(4);
            o.Retry.MaxRetryAttempts = 2;
        }).Services.AddHttpClient(GeminiVisionService.HttpClientName).AddHttpMessageHandler<AiMeterHandler>();

        // A clip takes up to a few minutes, and retrying one would bill twice: long timeout, no retries.
        services.AddHttpClient(OmniVideoService.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(15)).AddHttpMessageHandler<AiMeterHandler>();

        // A FLUX picture takes about 10 s, but a rate-limited one waits up to a minute per try.
        services.AddHttpClient(FluxImageService.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(3));
        services.AddHttpClient(SpokenRoastService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(90));

        AddVideo(services, configuration, environment);

        if (MockAi.IsEnabled(configuration, environment))
        {
            services.AddSingleton<IVisionService, MockVisionService>();
            services.AddSingleton<ICaptionWriter, MockCaptionWriter>();
            services.AddSingleton<IRunStep, MemeCaptionStep>();
            services.AddSingleton<IChatClient, MockChatClient>();
            services.AddSingleton<IMediaSummarizer, MockMediaSummarizer>();
            services.AddSingleton<IRoastCritic, MockRoastCritic>();
            services.AddSingleton<IImageGenerationService, MockImageGenerationService>();
            services.AddSingleton<ReproductionPromptWriter>();
            services.AddSingleton<IRunStep, RestyleStep>();
            services.AddSingleton<IRunStep, BulkStylesStep>();
            // No chat model in mock mode: the roast code then takes its own built-in fallbacks.
            services.AddSingleton<IChatCompletionService>(new ChatCompletions());
            services.AddSingleton<IMusicGenerationService, MockMusicService>();
            AddRoast(services);
            services.AddSingleton<IVideoGenerationService, MockVideoService>();
            services.AddSingleton<IRunStep, PhotoToVideoStep>();
            return services;
        }

        // Free-form text: Azure OpenAI. With a Google key too, Gemini answers what it fails.
        var chat = ChatClients.AzureOpenAi(configuration);
        var geminiChat = ProviderFallback.GeminiChat(configuration);

        // Looking at pictures: Gemini gives real sentences cheaply; Azure OpenAI is the fallback.
        var canSeeImages = Has(ConfigKeys.GoogleApiKey) || chat is not null;
        if (canSeeImages)
            services.AddSingleton<IVisionService>(s => Vision(s, configuration));

        // Every result gets a title and a one-line summary, from whichever chat model there is.
        if (chat is not null || geminiChat is not null)
            services.AddSingleton<IMediaSummarizer>(s => new ChatMediaSummarizer(
                chat is null ? Instrument(geminiChat!, s, "summary") : s.GetRequiredService<IChatClient>(), s.GetRequiredService<BlobStorageService>()));

        if (chat is not null)
        {
            services.AddSingleton(s => geminiChat is null
                ? Instrument(chat, s, "chat")
                : new FallbackChatClient(
                    Instrument(chat, s, "chat"), ProviderFallback.AzureOpenAi, Instrument(geminiChat, s, "chat-fallback"), ProviderFallback.Gemini,
                    s.GetRequiredService<ILogger<FallbackChatClient>>()));
            services.AddSingleton<AiJudge>();
            services.AddSingleton<IRoastCritic, ChatRoastCritic>();
            services.AddSingleton<ICaptionWriter, ChatCaptionWriter>();
            if (canSeeImages)
                services.AddSingleton<IRunStep, MemeCaptionStep>();

            if (Has(ConfigKeys.GoogleApiKey))
            {
                services.AddSingleton(s => ImageGeneration(s, configuration));
                services.AddSingleton<ReproductionPromptWriter>();
                services.AddSingleton<IRunStep, RestyleStep>();
            }
        }

        if (Has(ConfigKeys.GoogleApiKey))
        {
            services.TryAddSingleton<IImageGenerationService>(s => ImageGeneration(s, configuration));
            services.AddSingleton<IRunStep, BulkStylesStep>();
            services.AddSingleton<IVideoGenerationService, OmniVideoService>();
            services.AddSingleton<IRunStep, PhotoToVideoStep>();

            if (canSeeImages)
            {
                // The roast needs a performer (Lyria, on the Google key) and something that can see.
                // Without a chat model it still runs, on its built-in lyrics, and says so.
                services.AddSingleton<IChatCompletionService>(s => new ChatCompletions(chat is null ? null : s.GetRequiredService<IChatClient>()));
                // With a Foundry speech deployment too, a spoken set stands in when Lyria fails.
                services.AddSingleton<IMusicGenerationService>(s =>
                {
                    var lyria = ActivatorUtilities.CreateInstance<LyriaMusicService>(s);
                    return SpokenRoastService.IsAvailable(configuration)
                        ? new FallbackMusicService(
                            lyria, "Lyria", ActivatorUtilities.CreateInstance<SpokenRoastService>(s), "Azure speech",
                            s.GetRequiredService<ILogger<FallbackMusicService>>())
                        : lyria;
                });
                AddRoast(services);
            }
        }

        return services;

        bool Has(string key) => !string.IsNullOrWhiteSpace(configuration[key]);
    }

    /// <summary>
    /// The video functions: the sound library and the render are always here; the AI parts are
    /// the mocks, or the Foundry services when Foundry is configured, or absent.
    /// </summary>
    private static void AddVideo(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton<ISoundAssetRepository, SoundAssetTableRepository>();
        services.AddSingleton<ISemanticMatchingService, SemanticMatchingService>();
        services.AddSingleton<AiFoundryClient>();
        services.AddSingleton<FFmpegRenderService>();
        services.AddSingleton<IMediaToolkit>(s => s.GetRequiredService<FFmpegRenderService>());
        services.AddSingleton<SourceAudioAnalysis>();
        services.AddSingleton<ISourceAudioAnalysis>(s => s.GetRequiredService<SourceAudioAnalysis>());

        if (MockAi.IsEnabled(configuration, environment))
        {
            services.AddSingleton<IAiVisionService, MockFrameVision>();
            services.AddSingleton<IDirectorService, MockDirector>();
            services.AddSingleton<ITranscriptionService, MockTranscription>();
            services.AddSingleton<IVideoRoast, MockVideoRoast>();
        }
        else
        {
            // Registered either way: it reports itself disabled when no speech deployment is set.
            services.AddSingleton<ITranscriptionService, AiFoundryTranscriptionService>();
            var google = !string.IsNullOrWhiteSpace(configuration[ConfigKeys.GoogleApiKey]);
            var foundry = AiFoundryClient.Setting(configuration, "AiFoundry:Endpoint") is not null;

            // Gemini watches the whole clip, sound and all; Foundry's sampled frames are the fallback.
            if (google)
                services.AddSingleton<IClipVision, GeminiClipVisionService>();

            if (foundry)
            {
                services.AddSingleton<IAiVisionService, AiFoundryVisionService>();
                services.AddSingleton<IDirectorService, AiFoundryDirectorService>();
                services.AddSingleton<IVideoRoast, RoastService>();
            }
        }

        services.AddSingleton<IRunStep, VideoStep>();
    }

    private static void AddRoast(IServiceCollection services)
    {
        services.AddSingleton<SceneDescriber>();
        services.AddSingleton<RoastLyricsWriter>();
        services.AddSingleton<IRunStep, RapRoastStep>();
    }

    /// <summary>
    /// The one vision provider, with its answers cached by picture: Gemini, else Azure OpenAI. With
    /// both configured, Azure OpenAI answers what Gemini fails (and the answer is cached as Gemini's).
    /// </summary>
    private static CachingVisionService Vision(IServiceProvider services, IConfiguration configuration)
    {
        var azure = ChatClients.AzureOpenAi(configuration) is { } chat ? new ChatVisionService(Instrument(chat, services, "vision")) : null;
        if (string.IsNullOrWhiteSpace(configuration[ConfigKeys.GoogleApiKey]))
            return new CachingVisionService(azure!, services.GetRequiredService<HybridCache>(), "azure-openai");

        IVisionService gemini = new GeminiVisionService(configuration, services.GetRequiredService<IHttpClientFactory>());
        if (azure is not null)
            gemini = new FallbackVisionService(
                gemini, ProviderFallback.Gemini, azure, ProviderFallback.AzureOpenAi, services.GetRequiredService<ILogger<FallbackVisionService>>());
        return new CachingVisionService(gemini, services.GetRequiredService<HybridCache>(), "gemini");
    }

    /// <summary>Gemini draws; with a Foundry image deployment too, FLUX draws what Gemini fails (such as when its credit runs out).</summary>
    private static IImageGenerationService ImageGeneration(IServiceProvider services, IConfiguration configuration)
    {
        var gemini = ActivatorUtilities.CreateInstance<GeminiImageService>(services);
        return FluxImageService.IsConfigured(configuration)
            ? new FallbackImageService(
                gemini, ProviderFallback.Gemini, ActivatorUtilities.CreateInstance<FluxImageService>(services), "FLUX (Azure)",
                services.GetRequiredService<ILogger<FallbackImageService>>())
            : gemini;
    }

    /// <summary>A chat client with OpenTelemetry spans (gen_ai.*) and usage metering around it.</summary>
    private static IChatClient Instrument(IChatClient chat, IServiceProvider services, string stage) =>
        chat.AsBuilder()
            .UseOpenTelemetry(services.GetService<ILoggerFactory>(), PoTelemetry.Name)
            .Use(inner => new MeteredChatClient(inner, services.GetRequiredService<AiMeter>(), stage))
            .Build(services);
}
