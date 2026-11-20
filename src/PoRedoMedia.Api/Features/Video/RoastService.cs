using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Security;
using System.Text;
using System.Text.Json;
using PoRedoMedia.Api.Common.Ai;
using OpenAI.Chat;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Video;

/// <summary>
/// The roast voiceover: a stand-up comedian insulting what happens in the video, one joke per
/// moment, at most <see cref="MediaRoast.MaxTotalMs"/> of speech in all.
/// </summary>
/// <remarks>
/// Three steps: the chat deployment writes the jokes from the vision labels and the transcript the
/// engine already has (no second look at the frames); a voice engine performs each one; the clips
/// are placed on the timeline so none overlaps and none runs past the end. Every failure belongs
/// to the caller, which renders the video without a roast.
/// <para>
/// The voices are plain HTTP calls, not <see cref="AiFoundryClient"/>'s SDK client: the SDK's API
/// version answers 404 for a <c>gpt-4o-mini-tts</c> deployment, and Azure Speech and Lyria are not
/// OpenAI endpoints at all. <see cref="MediaRoast.Voices"/> offers none while AI calls are
/// intercepted, so a test host never reaches them.
/// </para>
/// </remarks>
public sealed partial class RoastService : IVideoRoast, IDisposable
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "AI usage: Stage=roast-voice Voice={Voice} Session={AiSessionId} Clips={Clips} Characters={Characters} ElapsedMs={ElapsedMs}")]
    private partial void LogUsage(string voice, MediaId aiSessionId, int clips, int characters, long elapsedMs);

    /// <summary>The API version that serves <c>audio/speech</c> for gpt-4o-mini-tts with <c>instructions</c>.</summary>
    private const string SpeechApiVersion = "2025-03-01-preview";

    private const string LyriaUrl = "https://generativelanguage.googleapis.com/v1beta/interactions";

    public void Dispose() => _http.Dispose();

    internal const int MaxJokes = 5;

    /// <summary>Longest joke, in characters. The comic takes about seven seconds over fourteen words.</summary>
    private const int MaxJokeLength = 160;

    /// <summary>Breath between two jokes that would otherwise run into each other.</summary>
    private const int GapMs = 250;

    private const int MaxMoments = 40;

    private const string Schema = """
        {"type":"object","properties":{"jokes":{"type":"array","items":{"type":"object","properties":{"moment":{"type":"integer"},"text":{"type":"string"},"alternative":{"type":"string"}},"required":["moment","text","alternative"],"additionalProperties":false}}},"required":["jokes"],"additionalProperties":false}
        """;

    /// <summary>How mean the jokes are, by <see cref="MemeMix.Meanness"/>.</summary>
    private static readonly string[] MeannessStyle =
    [
        "gentle, good-natured teasing: friendly ribbing that nobody would mind. Specific beats generic: name what is actually happening in that moment and poke fun at it.",
        "cheeky, playful teasing with a wink. Specific beats generic: name what is actually happening in that moment and tease it.",
        "biting, witty and sarcastic: a comedy-club roast. Specific beats generic: name what is actually happening in that moment and tease it.",
        "savage and cutting: every joke is a real insult. Specific beats generic: name what is actually happening in that moment and tear it apart.",
        "brutal and merciless, dripping with sarcasm: deadpan fake praise that is really an insult (Wow, truly groundbreaking work there), eye-rolling disbelief. Every joke must sting; a gentle or wholesome joke is a failure. Name what is actually happening in that moment and tear it apart.",
    ];

    /// <summary>How much the jokes swear, by <see cref="MemeMix.Swearing"/>.</summary>
    private static readonly string[] SwearingStyle =
    [
        "Language: without any swearing, vulgarity or profanity.",
        "Language: mild words only (damn, hell, crap), now and then; nothing stronger.",
        "Language: some swearing (shit, ass, damn, hell, the odd fuck) where it lands a punchline.",
        "Language: swear freely; most jokes curse (fuck, shit, ass, damn, hell).",
        "Language: vulgar and profane. This is an adult roast: every joke swears (fuck, shit, ass, damn, hell) and a clean joke is a failure.",
    ];

    /// <summary>The roast brief for a mix: its meanness and swearing, then the rules that hold at every level.</summary>
    internal static string PromptFor(MemeMix mix) => $$"""
        You are a stand-up comedian doing a roast of a video clip. You get a timeline of its moments: what the camera saw and what was said.
        Write short roast jokes, each aimed at ONE moment, to be spoken over the video at that moment.

        Style: {{MeannessStyle[mix.Meanness]}}
        {{SwearingStyle[mix.Swearing]}}
        Target: what people DO in the clip — their choices, their skill, their timing, their taste, the situation they got themselves into.
        Never: slurs; jokes about race, ethnicity, religion, gender, sexuality, disability or body size; sexual remarks about a person; threats or wishing harm. If a moment shows a child, roast the situation or the adults, not the child.

        {{InsultRule(mix)}}
        Each joke: one or two sentences, at most 14 words, written to be said aloud. No emoji, no hashtags, no stage directions, no quotation marks.
        Spread the jokes across the clip, in time order, at most one per moment. Skip moments with nothing to mock.
        For every joke also write an alternative: a second, different joke for the same moment, from another angle.
        Answer {"jokes":[{"moment": <index of the moment>, "text": "<the joke>", "alternative": "<another joke>"}]}.
        """;

    internal static string PromptFor(bool crude) => PromptFor(MemeMix.FromCrude(crude));

    internal static string RubricFor(MemeMix mix) => (mix.Swearing switch
    {
        0 => CleanJokeRubric,
        >= 3 => JokeRubric,
        _ => SomeSwearingRubric,
    }) + (mix.Meanness >= InsultFrom ? " " + InsultRubric : "");

    /// <summary>From "Sarcastic" up the roast is an insult roast; below it, teasing is the point.</summary>
    private const int InsultFrom = 2;

    /// <summary>
    /// What makes a line an insult, said to the writer. Without it a savage roast came back as "He leans in
    /// close like he's about to interrogate my sanity, not my ears": an observation with a simile, nobody
    /// put down, and the judge and the critic both passed it because neither was asked to look.
    /// </summary>
    internal static string InsultRule(MemeMix mix) => mix.Meanness >= InsultFrom
        ? "Every joke is an INSULT: a put-down of someone in the clip for what they are doing, said about them (he, she, they) or to them (you). Say what is bad, sad or embarrassing about it. A line that only describes or compares, with nobody put down, is a failure. Never speak about yourself (no I, me, my)."
        : "Every joke teases someone in the clip for what they are doing, said about them (he, she, they) or to them (you). Never speak about yourself (no I, me, my).";

    internal const string InsultRubric =
        "An insult roast: a line that puts nobody down (a description, a comparison, a remark about the speaker) scores lowest, however clever.";

    /// <summary>What the critic is told it is reading, so a set with no insults in it is sent back.</summary>
    internal static string CriticTask(MemeMix mix) => "Roast jokes, each spoken over one moment of a video clip"
        + (mix.Meanness >= InsultFrom ? ". This is an insult roast: a line that insults nobody is a weak line, and a set with any such line scores 5 or less" : "");

    internal static string RubricFor(bool crude) => RubricFor(MemeMix.FromCrude(crude));

    /// <summary>Seconds of clip per roast line, by <see cref="MemeMix.Jokes"/> (0 is no roast); a spoken joke runs about seven.</summary>
    private static readonly double[] SecondsPerJoke = [double.PositiveInfinity, 16, 11, 7, 5];

    /// <summary>The most roast lines a clip gets, by <see cref="MemeMix.Jokes"/>.</summary>
    internal static int MaxJokesFor(MemeMix mix, double durationSeconds)
        => mix.Jokes == 0 ? 0 : Math.Clamp((int)(durationSeconds / SecondsPerJoke[mix.Jokes]), 1, mix.Jokes == MemeMix.Max ? MaxJokes + 3 : MaxJokes);

    /// <summary>
    /// For a clip a narrator speaks over (a Create Movie clip): the roast is a heckler answering the narrator, not a
    /// second voice-over. Its lines are spoken in the narrator's pauses (<see cref="TurnTaking"/>).
    /// </summary>
    internal const string NarratorBrief =
        "The lines marked said: are a narrator's voice-over, telling the story of the clip. Play off the narrator like a heckler in the front row: answer, contradict or one-up what they just said, or mock how they put it. At least half of the jokes answer a said: line, aimed at that line's moment. Each joke is spoken in the pause right after the line it answers.\n";

    /// <summary>
    /// For a kids' video (Create Movie's Kids series): the owner wants the heckler meaner and drier than the default
    /// roast. The child is still never the target; the system prompt's rules hold.
    /// </summary>
    internal const string KidsBrief =
        "Aim at the grown-ups filming, the narrator, the toys, the mess, the plan and the production value.\n";

    /// <summary>What the roast picks on most, by <see cref="MemeMix.Target"/>; null for "mix", which leaves it to the comic.</summary>
    internal static string? TargetBrief(string target) => target switch
    {
        "adults" => "Aim most jokes at the grown-ups: whoever is filming, planning, helping or failing to supervise.\n",
        "narrator" => "Aim most jokes at the narrator: their lines, their enthusiasm, their facts and how they put things.\n",
        "things" => "Aim most jokes at the things in the clip: the toys, the gadgets, the mess, the set and the props.\n",
        "camera" => "Aim most jokes at the filming itself: the shaky camera, the angles, the lighting, the editing and the production value.\n",
        _ => null,
    };

    private const string ComicDelivery =
        "A brash stand-up comedian mid-set at a roast: loud, fast, incredulous, mocking. Land hard on the last words of the line.";

    private const string RapStyle =
        "Aggressive old-school boom-bap diss track. One male rapper, vocals loud and clear up front, hard drums. About 25 seconds.";

    // One client for the service's lifetime; each call brings its own timeout.
    private readonly HttpClient _http;
    private readonly AiFoundryClient _ai;
    private readonly IConfiguration _config;
    private readonly BlobStorageService _blobs;
    private readonly ILogger<RoastService> _logger;

    private readonly AiJudge? _judge;
    private readonly IRoastCritic? _critic;

    /// <param name="judge">When present, it picks between each joke and its alternative.</param>
    /// <param name="critic">When present, it scores the picked jokes, and a weak set is written once more.</param>
    public RoastService(
        AiFoundryClient ai, IConfiguration config, BlobStorageService blobs, ILogger<RoastService> logger, AiJudge? judge = null,
        IRoastCritic? critic = null)
    {
        _ai = ai;
        _judge = judge;
        _critic = critic;
        _http = new HttpClient(new AiMeterHandler(ai.Meter, config) { InnerHandler = new SocketsHttpHandler() }) { Timeout = Timeout.InfiniteTimeSpan };
        _config = config;
        _blobs = blobs;
        _logger = logger;
    }

    /// <summary>
    /// Writes, voices and stores the item's roast. Null when the clip gave nothing to roast or
    /// no joke fitted. <paramref name="labels"/> and <paramref name="transcript"/> are in output time.
    /// </summary>
    public async Task<RoastTrack?> GenerateAsync(
        MediaId mediaId,
        string voice,
        IReadOnlyList<SceneLabel> labels,
        IReadOnlyList<TranscriptSegmentDto> transcript,
        double durationSeconds,
        bool narrated,
        CancellationToken ct,
        MemeMix? mix = null,
        bool childIsIt = false)
    {
        var moments = Moments(labels, transcript);
        if (moments.Count == 0)
            return null;

        mix ??= MemeMix.FromCrude(true);
        if (mix.Jokes == 0)
            return null;
        var jokes = await WriteJokesAsync(mediaId, moments, durationSeconds, narrated, mix, childIsIt, ct);
        if (jokes.Count == 0)
            return null;

        var id = mediaId.Value;
        var sw = Stopwatch.StartNew();
        RoastTrack track;
        if (voice == MediaRoast.Rap)
        {
            // Lyria performs lyrics as one song, so the rap is a single clip from the top.
            var (audio, extension) = await SingAsync(jokes.Select(j => j.Text), ct);
            var path = MediaAnalysisPaths.RoastClip(id, 0, extension);
            await UploadAsync(path, audio, extension, ct);
            var text = string.Join(" / ", jokes.Select(j => j.Text));
            track = new RoastTrack(voice, path, [new RoastLine(0, AudioDuration.EstimateMs(audio, path), text, path)]);
        }
        else
        {
            // One at a time: a handful of short calls, and a burst would only meet the rate limit.
            // Speaking stops once the clips already fill the roast, so at most one goes unused.
            var videoMs = (long)(durationSeconds * 1000);
            var clips = new List<byte[]>();
            long spokenMs = 0;
            foreach (var joke in jokes.TakeWhile(_ => spokenMs < Math.Min(MediaRoast.MaxTotalMs, videoMs)))
            {
                clips.Add(await SpeakAsync(voice, joke.Text, ct));
                spokenMs += AudioDuration.EstimateMs(clips[^1], ".mp3");
            }

            var placed = Schedule(
                [.. clips.Select((clip, i) => (jokes[i].AtMs, AudioDuration.EstimateMs(clip, ".mp3")))],
                videoMs);
            if (placed.Count == 0)
                return null;

            var lines = new List<RoastLine>();
            using var whole = new MemoryStream();
            foreach (var (index, startMs) in placed)
            {
                var path = MediaAnalysisPaths.RoastClip(id, lines.Count, "mp3");
                await UploadAsync(path, clips[index], "mp3", ct);
                lines.Add(new RoastLine(startMs, AudioDuration.EstimateMs(clips[index], ".mp3"), jokes[index].Text, path));
                whole.Write(clips[index]);
            }

            // ponytail: the sound file is the clips' bytes back to back. MP3 frames stand alone and
            // every clip comes from one engine at one bitrate, so players read it as one file; an
            // ffmpeg concat (through IMediaToolkit) is the upgrade if a player ever disagrees.
            var audioPath = MediaAnalysisPaths.RoastAudio(id);
            await UploadAsync(audioPath, whole.ToArray(), "mp3", ct);
            track = new RoastTrack(voice, audioPath, lines);
        }

        LogUsage(voice, mediaId, track.Lines.Count, jokes.Sum(j => j.Text.Length), sw.ElapsedMilliseconds);
        await MediaRoast.SaveAsync(_blobs, mediaId, track, ct);
        return track;
    }

    /// <summary>What the comedian gets to work with: each vision label, and each line of speech at its end.</summary>
    internal static List<(double AtSeconds, string What)> Moments(IReadOnlyList<SceneLabel> labels, IReadOnlyList<TranscriptSegmentDto> transcript)
        => [.. labels
            .Where(l => !string.IsNullOrWhiteSpace(l.Label) && l.Label != "unknown")
            .Select(l => (l.TimestampSeconds, $"seen: {l.Label}"))
            // After the line, so the joke answers it instead of talking over it.
            .Concat(transcript.Select(s => (s.EndSeconds, $"said: \"{s.Text.Replace('"', '\'')}\"")))
            .OrderBy(m => m.Item1)
            .Take(MaxMoments)];

    private async Task<List<(long AtMs, string Text)>> WriteJokesAsync(
        MediaId mediaId, List<(double AtSeconds, string What)> moments, double durationSeconds, bool narrated, MemeMix mix, bool childIsIt, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        // By the roast-lines level. This used to be one per seven seconds whatever the level said, so
        // the slider changed nothing and a 15 s clip was asked for two lines at most.
        var max = MaxJokesFor(mix, durationSeconds);
        var prompt = new StringBuilder()
            .Append(inv, $"The clip is {durationSeconds:0.#} seconds long. Write {max} joke(s), fewer only when there are fewer moments.\n")
            .Append(narrated ? NarratorBrief : "")
            .Append(TargetBrief(mix.Target) ?? (childIsIt ? KidsBrief : ""))
            .Append(childIsIt ? "If you mention the little child, call it \"it\" or \"the little one\", never he, she, they, him, her, his, bro, boy, girl or king.\n" : "")
            .Append("\nMoments:\n");
        for (var i = 0; i < moments.Count; i++)
            prompt.Append(inv, $"[{i}] t={moments[i].AtSeconds:0.0}s {moments[i].What}\n");

        var jokes = ReadJokes(await AskAsync(prompt.ToString()), moments, max);
        List<(long AtMs, string Text)> picked = _judge is null ? [.. jokes.Select(j => (j.AtMs, j.Text))] : await PickJokesAsync(_judge, jokes, mix, ct);
        if (picked.Count == 0)
            return picked;

        // The quality pass: a set the critic scores as weak is written once more, with its note.
        var draft = string.Join('\n', picked.Select(j => j.Text));
        List<(long AtMs, string Text)> rewritten = [];
        var (_, critique, better) = await RoastQuality.ImproveOnceAsync(
            _critic, CriticTask(mix), draft,
            async (verdict, token) =>
            {
                rewritten = [.. ReadJokes(await AskAsync($"{prompt}\n{RoastQuality.RewriteBrief(verdict, draft)}", token), moments, max)
                    .Select(j => (j.AtMs, j.Text))];
                return rewritten.Count == 0 ? null : string.Join('\n', rewritten.Select(j => j.Text));
            },
            _logger, ct);
        if (critique is not null)
            _logger.LogInformation("Roast critic scored the jokes {Score}/10; rewritten: {Rewritten}", critique.Score, better);
        return better ? rewritten : picked;

        Task<string> AskAsync(string user, CancellationToken? token = null) => _ai.CompleteJsonAsync(
            new AiCall("roast", _ai.Deployment, "roast", Schema)
            {
                MediaId = mediaId,
                Temperature = 1.0f,
                MaxOutputTokens = 2_500,
            },
            [new SystemChatMessage(PromptFor(mix)), new UserChatMessage(user)],
            token ?? ct);
    }

    internal const string JokeRubric =
        "Funniest, and most specific to its moment; at most 14 words; written to be said aloud. Profanity is "
        + "expected. Safety: aims at what people DO, never at race, ethnicity, religion, gender, sexuality, "
        + "disability or body size; no sexual remarks about a person; a child is never the target.";

    internal const string SomeSwearingRubric =
        "Funniest, and most specific to its moment; at most 14 words; written to be said aloud. Some swearing is fine, "
        + "not required. Safety: aims at what people DO, never at race, ethnicity, religion, gender, sexuality, "
        + "disability or body size; no sexual remarks about a person; a child is never the target.";

    internal const string CleanJokeRubric =
        "Funniest, and most specific to its moment; at most 14 words; written to be said aloud. Keep it clean: no "
        + "profanity or vulgarity. Safety: aims at what people DO, never at race, ethnicity, religion, gender, "
        + "sexuality, disability or body size; no sexual remarks about a person; a child is never the target.";

    /// <summary>Each joke or its alternative, whichever the judge scores higher. One judge call for the whole set.</summary>
    private static async Task<List<(long AtMs, string Text)>> PickJokesAsync(
        AiJudge judge, List<(long AtMs, string Text, string? Alternative, string Moment)> jokes, MemeMix mix, CancellationToken ct)
    {
        var drafts = jokes.SelectMany(j => j.Alternative is null ? [$"({j.Moment}) {j.Text}"] : new[] { $"({j.Moment}) {j.Text}", $"({j.Moment}) {j.Alternative}" }).ToList();
        double[] scores;
        try
        {
            scores = await judge.ScoreAsync("Roast jokes, each spoken over one moment of a video clip", RubricFor(mix), drafts, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return [.. jokes.Select(j => (j.AtMs, j.Text))];
        }

        var picked = new List<(long, string)>();
        var i = 0;
        foreach (var joke in jokes)
        {
            var first = scores[i++];
            picked.Add(joke.Alternative is not null && scores[i++] > first ? (joke.AtMs, joke.Alternative) : (joke.AtMs, joke.Text));
        }

        return picked;
    }

    /// <summary>The jokes in the model's answer, each at its moment's time: one per moment, in time order.</summary>
    internal static List<(long AtMs, string Text, string? Alternative, string Moment)> ReadJokes(
        string raw, IReadOnlyList<(double AtSeconds, string What)> moments, int max)
    {
        using var doc = JsonDocument.Parse(AiFoundryClient.StripCodeFence(raw));
        var array = doc.RootElement.ValueKind == JsonValueKind.Object
            ? doc.RootElement.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Array).Value
            : doc.RootElement;
        if (array.ValueKind != JsonValueKind.Array)
            return [];

        var byMoment = new SortedDictionary<double, (string Text, string? Alternative, string Moment)>();
        foreach (var joke in array.EnumerateArray())
        {
            if (joke.ValueKind != JsonValueKind.Object
                || !joke.TryGetProperty("moment", out var moment) || !moment.TryGetInt32(out var index)
                || index < 0 || index >= moments.Count
                || !joke.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String
                || UserText.Clean(text.GetString(), MaxJokeLength) is not { } clean)
            {
                continue;
            }

            var alternative = joke.TryGetProperty("alternative", out var alt) && alt.ValueKind == JsonValueKind.String
                ? UserText.Clean(alt.GetString(), MaxJokeLength)
                : null;
            byMoment.TryAdd(moments[index].AtSeconds, (clean, alternative, moments[index].What));
        }

        return [.. byMoment.Take(max).Select(j => ((long)Math.Round(j.Key * 1000), j.Value.Text, j.Value.Alternative, j.Value.Moment))];
    }

    /// <summary>
    /// Where each clip plays: at its moment, pushed later while the previous joke is still being
    /// said, pulled earlier when it would run past the end. A clip that fits neither way, or would
    /// take the roast past <see cref="MediaRoast.MaxTotalMs"/>, is left out.
    /// </summary>
    /// <param name="clips">In time order.</param>
    internal static List<(int Index, long StartMs)> Schedule(IReadOnlyList<(long AtMs, int DurationMs)> clips, long videoMs)
    {
        var placed = new List<(int, long)>();
        long cursor = 0;
        var total = 0;
        for (var i = 0; i < clips.Count; i++)
        {
            var (at, duration) = clips[i];
            var start = Math.Min(Math.Max(at, cursor), videoMs - duration);
            if (duration <= 0 || start < cursor || total + duration > MediaRoast.MaxTotalMs)
                continue;

            placed.Add((i, start));
            cursor = start + duration + GapMs;
            total += duration;
        }

        return placed;
    }

    private Task<byte[]> SpeakAsync(string voice, string text, CancellationToken ct)
    {
        var endpoint = AiFoundryClient.Setting(_config, "AiFoundry:Endpoint")!.TrimEnd('/');
        var key = AiFoundryClient.Setting(_config, "AiFoundry:Key");
        HttpRequestMessage request;
        if (voice == MediaRoast.Comic)
        {
            var deployment = AiFoundryClient.Setting(_config, "AiFoundry:TtsDeployment");
            request = new(HttpMethod.Post, $"{endpoint}/openai/deployments/{deployment}/audio/speech?api-version={SpeechApiVersion}")
            {
                Content = JsonContent.Create(new { model = deployment, input = text, voice = "ash", instructions = ComicDelivery, response_format = "mp3" }),
            };
            request.Headers.Add("api-key", key);
        }
        else
        {
            // Azure Speech on the same AI Services resource. The text is model output going into
            // XML, so it is escaped.
            var ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='https://www.w3.org/2001/mstts' xml:lang='en-US'>"
                + "<voice name='en-US-DavisNeural'><mstts:express-as style='excited'><prosody rate='+8%'>"
                + SecurityElement.Escape(text)
                + "</prosody></mstts:express-as></voice></speak>";
            request = new(HttpMethod.Post, $"{endpoint}/tts/cognitiveservices/v1")
            {
                Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml"),
            };
            request.Headers.Add("Ocp-Apim-Subscription-Key", key);
            request.Headers.Add("X-Microsoft-OutputFormat", "audio-24khz-96kbitrate-mono-mp3");
            request.Headers.Add("User-Agent", "poredomedia");
        }

        return SendAsync(request, voice, TimeSpan.FromSeconds(60), ct);
    }

    /// <summary>The jokes as one sung track from Google Lyria; the audio and its file extension.</summary>
    private async Task<(byte[] Audio, string Extension)> SingAsync(IEnumerable<string> jokes, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, LyriaUrl)
        {
            Content = JsonContent.Create(new
            {
                model = AiFoundryClient.Setting(_config, "Google:LyriaModel") ?? "lyria-3-clip-preview",
                input = $"{RapStyle}\n\nPerform these lyrics exactly as written:\n\n[Verse]\n{string.Join('\n', jokes)}",
                response_format = new { type = "audio" },
            }),
        };
        request.Headers.Add("x-goog-api-key", AiFoundryClient.Setting(_config, "Google:ApiKey"));

        // A song takes Lyria 30–90 seconds.
        var body = await SendAsync(request, MediaRoast.Rap, TimeSpan.FromSeconds(150), ct);
        using var doc = JsonDocument.Parse(body);
        return FindAudio(doc.RootElement)
            ?? throw new InvalidOperationException("Lyria returned no audio (its safety filter declines some lyrics).");
    }

    /// <summary>
    /// The first base64 audio payload anywhere in a Lyria answer. The preview API has moved the
    /// field around (<c>data</c>, <c>audio</c>, <c>bytesBase64Encoded</c>), so the search is by shape.
    /// </summary>
    internal static (byte[] Audio, string Extension)? FindAudio(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array)
            return node.EnumerateArray().Select(FindAudio).FirstOrDefault(a => a is not null);
        if (node.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in (string[])["data", "audio", "b64_audio", "bytesBase64Encoded"])
        {
            if (node.TryGetProperty(name, out var data) && data.ValueKind == JsonValueKind.String
                && data.TryGetBytesFromBase64(out var audio) && audio.Length > 0)
            {
                var mime = node.TryGetProperty("mime_type", out var m) || node.TryGetProperty("mimeType", out m) ? m.GetString() : null;
                return (audio, mime?.Contains("wav", StringComparison.OrdinalIgnoreCase) == true ? "wav" : "mp3");
            }
        }

        return node.EnumerateObject().Select(p => FindAudio(p.Value)).FirstOrDefault(a => a is not null);
    }

    private async Task<byte[]> SendAsync(HttpRequestMessage request, string voice, TimeSpan timeout, CancellationToken ct)
    {
        using (request)
        using (var timed = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timed.CancelAfter(timeout);
            using var response = await _http.SendAsync(request, timed.Token);
            var body = await response.Content.ReadAsByteArrayAsync(timed.Token);
            if (response.IsSuccessStatusCode)
                return body;

            var error = Encoding.UTF8.GetString(body.AsSpan(0, Math.Min(body.Length, 300)));
            _logger.LogWarning("Roast voice {Voice} answered {Status}: {Error}", voice, (int)response.StatusCode, error);
            throw new InvalidOperationException($"The {voice} voice answered {(int)response.StatusCode}.");
        }
    }

    private async Task UploadAsync(string path, byte[] audio, string extension, CancellationToken ct)
    {
        using var stream = new MemoryStream(audio);
        await _blobs.UploadAsync(path, stream, extension == "wav" ? "audio/wav" : "audio/mpeg", ct);
    }
}
