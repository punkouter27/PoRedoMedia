using System.Security.Cryptography;
using System.Text;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Movie.Library;
using PoRedoMedia.Movie.Pictures;
using PoRedoMedia.Movie.Render;
using PoRedoMedia.Movie.Script;
using PoRedoMedia.Movie.Voice;

namespace PoRedoMedia.Api.Features.Movies;

/// <summary>
/// The movie engine as this server can run it: where movies are kept, which programs and services were found, and the
/// engine's parts built on them. Whatever is missing has a stand-in or is simply not offered, so Create Movie always
/// opens; <see cref="Notice"/> says what is simulated.
/// </summary>
/// <remarks>
/// Movies live in folders on this server's own disk, one folder a movie, under one folder a user: the engine reads
/// and writes plain files, and FFmpeg must have them beside it.
/// ponytail: one server's disk, so a second instance would not see the first one's movies. Mirror the folders to
/// blob storage (pull on open, push after each change) to lift that.
/// </remarks>
public sealed class MovieEngine
{
    /// <summary>Who is spending, for the calls made on this request or job: its name in the spend log, how much, and its price.</summary>
    public static readonly AsyncLocal<Action<string, int, decimal>?> Used = new();

    private readonly (JsonChat Chat, SeeingChat See)? _chats;
    private readonly ImageMaker? _pictures;
    private readonly Func<string, string, CancellationToken, Task>? _footage;
    private readonly PexelsStock? _stock;
    private readonly SceneRenderer? _scenes;
    private readonly (string Id, string Secret)? _youTube;
    private readonly IHttpClientFactory _http;

    public MovieEngine(IConfiguration config, IHostEnvironment environment, IServiceProvider services, IHttpClientFactory http)
    {
        // On App Service only /home outlives a restart or a redeploy, so movies are kept there.
        var appService = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID")) && Directory.Exists("/home");
        Root = AiFoundryClient.Setting(config, MovieKeys.Root)
            ?? (environment.IsEnvironment(PoEnvironments.Test) ? Path.Combine(Path.GetTempPath(), "PoRedoMedia", "movies-test")
                : appService ? "/home/data/poredomedia/movies"
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoRedoMedia", "movies"));
        Directory.CreateDirectory(Root);
        _http = http;

        // The same FFmpeg the rest of the app renders with: the configured folder, the copy shipped beside the app, then PATH.
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        var ffmpeg = FfmpegRunner.Locate(AiFoundryClient.Setting(config, "FFmpegBinPath")) ?? FfmpegRunner.Locate(bundled);
        FfmpegAvailable = ffmpeg is not null;
        Runner = new FfmpegRunner(ffmpeg ?? "ffmpeg");
        if (File.Exists(FFmpegProcess.BundledFontPath))
            Fonts.Bundled = FFmpegProcess.BundledFontPath;
        Durations.WordsPerMinute = JsonFile.Load(PaceFile, Durations.DefaultWordsPerMinute);
        MemePrice = config.GetValue<decimal?>(MovieKeys.MemePrice) ?? MemePrice;
        _ = Task.Run(() => Files.SweepScratch(TimeSpan.FromDays(1), EpisodeBuilder.ClipCacheName));

        var mock = MockAi.IsEnabled(config, environment);
        var chatModel = AiFoundryClient.Setting(config, MovieKeys.ChatDeployment) ?? AiFoundryClient.Setting(config, ConfigKeys.OpenAiChatDeployment) ?? ChatClients.DefaultDeployment;
        Settings = new AppSettings(chatModel, AiFoundryClient.Setting(config, MovieKeys.FastChatDeployment) ?? chatModel)
        {
            ChatRates = TokenRates.Parse(config[MovieKeys.ChatRates] ?? ""),
            FastChatRates = TokenRates.Parse(config[MovieKeys.FastChatRates] ?? ""),
            VoiceRate = config.GetValue<decimal?>(MovieKeys.VoiceRate),
        };

        if (!mock && MovieAi.ChatResource(config) is var (chatEndpoint, chatKey))
            _chats = MovieAi.Chats(chatEndpoint, chatKey, (deployment, input, cached, output) =>
            {
                var rates = deployment == Settings.FastChatDeployment ? Settings.FastChatRates : Settings.ChatRates;
                Used.Value?.Invoke(SpendLog.ScriptTokens, input + output, rates?.Cost(input, cached, output) ?? 0);
                if (cached > 0)
                    Used.Value?.Invoke(SpendLog.CachedScriptTokens, cached, 0); // shows whether the long, unchanging prompt is being reused
            });

        INarrator narrator = new MockNarrator();
        if (!mock && MovieAi.SpeechResource(config) is var (speechEndpoint, speechKey))
        {
            if (AiFoundryClient.Setting(config, "AiFoundry:TranscriptionDeployment") is { } whisper)
                Hear = MovieAi.Listener(http, speechEndpoint, speechKey, whisper, () => Used.Value);
            if (AiFoundryClient.Setting(config, ConfigKeys.AiFoundryTtsDeployment) is { } speech)
            {
                narrator = new SpeechNarrator(http, Runner, speechEndpoint, speechKey, speech, AiFoundryClient.Setting(config, MovieKeys.Voice) ?? "ash", Hear, Settings.VoiceRate)
                {
                    Used = (what, units, dollars) => Used.Value?.Invoke(what, units, dollars),
                };
                VoiceLive = true;
            }
        }
        var paceGate = new Lock(); // recordings finish side by side
        Builder = new EpisodeBuilder(narrator, Runner)
        {
            PaceLearned = VoiceLive ? pace => { lock (paceGate) JsonFile.Save(PaceFile, pace); } : null,
        };

        // Pictures come from whichever picture provider the app has; footage needs the video provider as well.
        if (services.GetService<IImageGenerationService>() is { } images)
        {
            _pictures = MovieAi.Pictures(images);
            ImageModel = mock ? "mock" : AiFoundryClient.Setting(config, ConfigKeys.GoogleApiKey) is not null
                ? AiFoundryClient.Setting(config, ConfigKeys.GoogleImageModel) ?? "gemini-2.5-flash-image"
                : AiFoundryClient.Setting(config, ConfigKeys.AiFoundryImageDeployment) ?? "picture model";
            if (services.GetService<IVideoGenerationService>() is { } video)
                _footage = MovieAi.Footage(images, video);
            CostEstimate.HostPicturePrice = config.GetValue<decimal?>("AiPricing:ImageUsd");
            CostEstimate.VideoPrice = (config.GetValue<decimal?>("AiPricing:VideoUsd") ?? 0.80m) + (CostEstimate.HostPicturePrice ?? 0);
        }
        if (!mock && AiFoundryClient.Setting(config, MovieKeys.PexelsKey) is { } pexels)
            _stock = new PexelsStock(pexels, http.CreateClient(MovieAi.HttpClientName));
        if (FfmpegAvailable && SceneRenderer.Locate(AiFoundryClient.Setting(config, MovieKeys.Browser)) is { } browser)
            _scenes = new SceneRenderer(browser, Runner);
        if (AiFoundryClient.Setting(config, MovieKeys.YouTubeClientId) is { } id && AiFoundryClient.Setting(config, MovieKeys.YouTubeClientSecret) is { } secret)
            _youTube = (id, secret);

        Notice = string.Join(" ", new[]
        {
            FfmpegAvailable ? null : "FFmpeg was not found on this server, so nothing can be drawn or rendered.",
            ScriptLive ? null : "No script model is configured, so scripts are stand-in words.",
            VoiceLive ? null : "No voice service is configured, so the voice is a stand-in tone.",
            PicturesAvailable ? null : "No picture service is configured, so clips show their titles or your own films.",
        }.OfType<string>()) is { Length: > 0 } notice ? notice : null;
    }

    /// <summary>Under this folder, one folder a user.</summary>
    public string Root { get; }

    public bool FfmpegAvailable { get; }

    public FfmpegRunner Runner { get; }

    public AppSettings Settings { get; }

    public EpisodeBuilder Builder { get; }

    public Hear? Hear { get; }

    public bool ScriptLive => _chats is not null;

    public bool VoiceLive { get; }

    public bool PicturesAvailable => _pictures is not null;

    public bool YouTubeAvailable => _youTube is not null;

    public string ImageModel { get; } = "none";

    /// <summary>About what Meme-ify costs for one clip: looking at it, directing it, and writing and speaking a roast.</summary>
    public decimal MemePrice { get; } = 0.03m;

    public string? Notice { get; }

    private string PaceFile => Path.Combine(Root, "pace.json");

    /// <summary>A user's own folder, named so that nothing of who they are shows on disk.</summary>
    public string UserFolder(UserId user) =>
        Path.Combine(Root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user.Value)))[..32].ToLowerInvariant());

    /// <summary>
    /// A script writer for one user. Each has their own because a writer remembers which series it is writing for;
    /// they all ask the same model.
    /// </summary>
    /// <param name="note">Told, in words fit to show, of anything out of the ordinary in how a script came to be written.</param>
    public IScriptWriter NewWriter(Action<string> note)
    {
        if (_chats is not var (chat, see))
            return new MockScriptWriter();
        var writer = new ChatScriptWriter(Settings, chat) { See = see, Note = note };
        // The model is offered only the picture types this server can make.
        writer.CanMake = kind => VisualsFor(Root, Root, Quality.Medium, writer)?.CanMake(kind) ?? false;
        return writer;
    }

    /// <summary>
    /// What draws a movie's pictures into <paramref name="folder"/>, or null when no picture service is configured.
    /// Each picture really made is added to that folder's spending.
    /// </summary>
    /// <param name="hostFolder">Where the user's locked host design is kept.</param>
    /// <param name="writer">Writes code-drawn scenes and thinks again about a declined picture.</param>
    public Visuals? VisualsFor(string folder, string hostFolder, Quality quality, IScriptWriter writer) =>
        _pictures is null ? null : new Visuals(_pictures, new MediaCache(Path.Combine(folder, "images")), Path.Combine(hostFolder, "sheet.png"), ImageModel)
        {
            Quality = quality,
            Subjects = new MediaCache(Path.Combine(folder, "subject")),
            PictureMade = CostEstimate.PriceOf(ImageModel, quality) is { } dollars ? () => SpendLog.Add(folder, SpendLog.Picture, dollars) : null,
            VideoMade = () => SpendLog.Add(folder, SpendLog.AiVideo, CostEstimate.VideoPrice - (CostEstimate.HostPicturePrice ?? 0)),
            Stock = _stock is null ? null : _stock.FindAsync,
            Video = _footage is null ? null : async (shot, output, ct) =>
            {
                await _footage(shot, output, ct);
                if (CostEstimate.HostPicturePrice is { } still) SpendLog.Add(folder, SpendLog.Picture, still); // the picture it was set moving from
            },
            Scene = _scenes is null ? null : (request, output, ct) => DrawSceneAsync(writer, _scenes, request, output, ct),
            Rethink = writer.RethinkPictureAsync,
        };

    public YouTube? YouTube() => _youTube is var (id, secret) ? new YouTube(id, secret, _http.CreateClient(MovieAi.UploadClientName).SendAsync) : null;

    /// <summary>
    /// Has a scene written and drawn. Code that does not run is sent back once with what the browser said was wrong,
    /// which puts most of them right. Code that runs can still draw a label off the frame or under the captions, so a
    /// frame of it once built is looked at, and a fault found is sent back once.
    /// </summary>
    private async Task DrawSceneAsync(IScriptWriter script, SceneRenderer scenes, SceneRequest request, string output, CancellationToken ct)
    {
        var scene = await script.WriteSceneAsync(request, ct);
        try
        {
            await scenes.RenderAsync(scene, request.Length, output, ct);
        }
        catch (SceneCodeException e)
        {
            await scenes.RenderAsync(await script.WriteSceneAsync(request, ct, scene, e.Detail), request.Length, output, ct);
            return; // already sent back once
        }

        var (frame, better) = (Files.NewScratchFile(".png"), Files.NewScratchFile(".mp4"));
        try
        {
            // Once it has built: it builds until about 85% of its length and then holds.
            await Runner.RunAsync(FfmpegArgs.Still(output, request.Length * 0.9, 0, frame), Path.GetDirectoryName(frame)!, null, null, ct);
            if (await script.CheckSceneAsync(frame, ct) is { Fine: false } verdict)
            {
                await scenes.RenderAsync(await script.WriteSceneAsync(request, ct, scene, verdict.Problem), request.Length, better, ct);
                File.Move(better, output, overwrite: true);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The scene that was drawn runs: it is kept, faults and all, rather than nothing.
        }
        finally
        {
            File.Delete(frame);
            File.Delete(better);
        }
    }
}

/// <summary>Configuration keys Create Movie adds. Every one is optional.</summary>
public static class MovieKeys
{
    /// <summary>The folder movies are kept under. Empty = the server's local application data folder.</summary>
    public const string Root = "Movie:Root";

    /// <summary>The model that plans and writes a script. Empty = the app's chat deployment.</summary>
    public const string ChatDeployment = "Movie:ChatDeployment";

    /// <summary>The model for the small jobs (checking, watching frames, upload notes). Empty = the script model.</summary>
    public const string FastChatDeployment = "Movie:FastChatDeployment";

    /// <summary>The narrator's voice, one the speech deployment knows. Empty = "ash".</summary>
    public const string Voice = "Movie:Voice";

    /// <summary>Dollars a million tokens as "input,cached,output", so script use is shown priced.</summary>
    public const string ChatRates = "Movie:ChatRates";

    public const string FastChatRates = "Movie:FastChatRates";

    /// <summary>Dollars a million characters spoken.</summary>
    public const string VoiceRate = "Movie:VoiceRate";

    /// <summary>About what Meme-ify costs for one clip, in dollars, for the estimate shown before rendering. Empty = 0.03.</summary>
    public const string MemePrice = "Movie:MemeifyUsd";

    /// <summary>A Pexels key: stock photos are offered when it is set.</summary>
    public const string PexelsKey = "Pexels:ApiKey";

    /// <summary>A Chromium-family browser for code-drawn scenes. Empty = Edge or Chromium where the system keeps it.</summary>
    public const string Browser = "Movie:BrowserPath";

    /// <summary>A Google OAuth client of the "Web application" kind; uploading to YouTube is offered when both are set.</summary>
    public const string YouTubeClientId = "YouTube:ClientId";

    public const string YouTubeClientSecret = "YouTube:ClientSecret";
}
