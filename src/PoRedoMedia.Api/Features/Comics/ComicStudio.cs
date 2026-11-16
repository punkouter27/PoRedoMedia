using System.Collections.Concurrent;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Api.Features.Render;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;
using SkiaSharp;

namespace PoRedoMedia.Api.Features.Comics;

/// <summary>
/// Comic-ify: a pasted conversation becomes a comic strip and a narrated video of it, both put in the Library. The
/// script is written, each panel drawn and lettered, every line voiced (the narrator reads the caption boxes), meme
/// sounds and a music bed matched from the sound library, and the panels rendered one after another. One comic is one
/// run of the daily quota, refunded when it ends without one.
/// </summary>
public sealed partial class ComicStudio(
    ComicWriter writer,
    ISpeechSynthesis speech,
    ISoundAssetRepository sounds,
    ISemanticMatchingService matcher,
    BlobStorageService blobs,
    IMediaRepository media,
    Thumbnails thumbnails,
    FFmpegProcess ffmpeg,
    IRenderQuota quota,
    IHostApplicationLifetime lifetime,
    ILogger<ComicStudio> logger,
    IImageGenerationService? images = null)
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Comic for {User} failed")]
    private partial void LogFailed(Exception ex, UserId user);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Comic sound {Cue} could not be fetched")]
    private partial void LogSoundSkipped(Exception ex, string cue);

    private const int DrawAtOnce = 3, SpeakAtOnce = 4;
    private const double Lead = 0.5, Gap = 0.35, Tail = 0.8, ShortestPanel = 2.8;

    // ponytail: the jobs and the one-at-a-time gate are in this process only, like the run queue. A restart forgets a
    // comic under way (its quota stays spent) and two instances would each render one at a time.
    private readonly ConcurrentDictionary<string, ComicJobDto> _jobs = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public bool Available => images is not null;

    public ComicConfigDto Config() => new(
        Available,
        speech.IsConfigured,
        !Available ? "Comic-ify needs a picture service (Google:ApiKey), and none is configured on this server."
        : !speech.IsConfigured ? "No voice is configured on this server, so the video's panels are timed but silent apart from sounds and music."
        : null);

    public ComicJobDto? Job(UserId user) => _jobs.TryGetValue(user.Value, out var job) ? job : null;

    /// <returns>The job as it starts, or why it cannot and the status to answer with.</returns>
    public async Task<(ComicJobDto? Job, string? Error, int Status)> StartAsync(UserId user, string conversation, CancellationToken ct)
    {
        if (images is null)
            return (null, Config().Notice, StatusCodes.Status400BadRequest);
        conversation = conversation.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (conversation.Length < ComicRequest.MinLength)
            return (null, "Paste a conversation of at least a couple of lines.", StatusCodes.Status400BadRequest);
        if (conversation.Length > ComicRequest.MaxLength)
            return (null, $"That is too long for one comic: keep it under {ComicRequest.MaxLength:N0} characters.", StatusCodes.Status400BadRequest);
        if (Job(user) is { Working: true })
            return (null, "Your last comic is still being made. Wait for it to finish.", StatusCodes.Status409Conflict);
        var (allowed, _) = await quota.TryConsumeAsync(user, ct);
        if (!allowed)
            return (null, "You have used today's runs. Come back tomorrow.", StatusCodes.Status429TooManyRequests);

        var job = new ComicJobDto("Working", "Waiting for another comic to finish", 0, null, null, null, null, DateTimeOffset.UtcNow);
        _jobs[user.Value] = job;
        _ = Task.Run(() => RunAsync(user, conversation, images, lifetime.ApplicationStopping), CancellationToken.None);
        return (job, null, StatusCodes.Status200OK);
    }

    private void Report(UserId user, string step, double progress, string? title = null) =>
        _jobs.AddOrUpdate(user.Value, _ => new("Working", step, progress, title, null, null, null, DateTimeOffset.UtcNow),
            (_, job) => job with { Step = step, Progress = progress, Title = title ?? job.Title });

    private async Task RunAsync(UserId user, string conversation, IImageGenerationService pictures, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"poredomedia-comic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            await Gate.WaitAsync(ct);
            try
            {
                var (strip, video) = await MakeAsync(user, conversation, pictures, folder, ct);
                _jobs[user.Value] = Job(user)! with { State = "Done", Step = "Done", Progress = 1, StripId = strip, VideoId = video };
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (Exception e)
        {
            if (e is not OperationCanceledException)
                LogFailed(e, user);
            await quota.RefundAsync(user, CancellationToken.None);
            var reason = e switch
            {
                OperationCanceledException => "The server restarted before the comic was finished. Your run was given back.",
                RunStepException or ComicException => e.Message + " Your run was given back.",
                _ => "The comic could not be made. Your run was given back; try again in a minute.",
            };
            _jobs[user.Value] = (Job(user) ?? new("Failed", "", 0, null, null, null, null, DateTimeOffset.UtcNow)) with { State = "Failed", Error = reason };
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<(Guid Strip, Guid Video)> MakeAsync(UserId user, string conversation, IImageGenerationService pictures, string folder, CancellationToken ct)
    {
        Report(user, "Reading the conversation", 0.03);
        var script = await writer.WriteAsync(conversation, ct);
        Report(user, $"Drawing {script.Panels.Count} panels", 0.1, script.Title);

        // ---- Panels ----
        var lettered = new byte[script.Panels.Count][];
        var drawn = 0;
        using (var drawing = new SemaphoreSlim(DrawAtOnce))
        {
            await Task.WhenAll(script.Panels.Select(async (panel, i) =>
            {
                await drawing.WaitAsync(ct);
                try
                {
                    var art = await DrawAsync(pictures, PanelPrompt(script, panel, i), ct);
                    lettered[i] = ComicLettering.LetterPanel(art, panel);
                    await File.WriteAllBytesAsync(Path.Combine(folder, $"panel-{i}.png"), lettered[i], ct);
                    var done = Interlocked.Increment(ref drawn);
                    Report(user, $"Drew panel {done} of {script.Panels.Count}", 0.1 + 0.5 * done / script.Panels.Count);
                }
                finally
                {
                    drawing.Release();
                }
            }));
        }
        var stripPng = ComicLettering.Strip(lettered, script.Title);

        // ---- Voices ----
        Report(user, speech.IsConfigured ? "Recording the voices" : "Timing the lines", 0.62);
        var spoken = await VoiceAsync(script, folder, ct);

        // ---- Sounds and music ----
        Report(user, "Picking sounds and music", 0.75);
        var (cues, music) = await SoundsAsync(user, script, folder, ct);

        // ---- The video ----
        var clips = new List<string>();
        var total = 0.0;
        for (var i = 0; i < script.Panels.Count; i++)
        {
            Report(user, $"Rendering panel {i + 1} of {script.Panels.Count}", 0.78 + 0.14 * i / script.Panels.Count);
            var wav = Path.Combine(folder, $"panel-{i}.wav");
            await FfmpegAsync(FFmpegArgs.ComicPanelAudioArgs(spoken[i], cues[i], Lead, Gap, Tail, wav), ct);
            var seconds = Math.Max(ShortestPanel, await ffmpeg.DurationSecondsAsync(wav, ct));
            var clip = Path.Combine(folder, $"clip-{i}.mp4");
            await FfmpegAsync(FFmpegArgs.ComicPanelVideoArgs(Path.Combine(folder, $"panel-{i}.png"), wav, seconds, clip), ct);
            clips.Add(clip);
            total += seconds;
        }
        Report(user, "Putting the video together", 0.93);
        var list = Path.Combine(folder, "clips.txt");
        await File.WriteAllTextAsync(list, FFmpegArgs.ComicConcatList(clips), ct);
        var mp4 = Path.Combine(folder, "comic.mp4");
        await FfmpegAsync(FFmpegArgs.ComicJoinArgs(list, music, total, mp4), ct);

        // ---- The Library ----
        Report(user, "Saving to your Library", 0.96);
        var transcript = script.Transcript();
        var stripItem = new MediaItem
        {
            Owner = user, Id = MediaId.New(), Kind = MediaKind.Image, Status = MediaStatus.Ready, Origin = "Comic",
            Title = script.Title, ContentType = "image/png", Extension = ".png", SizeBytes = stripPng.Length,
            Text = transcript, Summary = $"A {script.Panels.Count}-panel comic strip made from a conversation.", CreatedAt = DateTimeOffset.UtcNow,
        };
        await blobs.UploadAsync(stripItem.SourcePath, stripPng, stripItem.ContentType, ct);
        await thumbnails.CreateAsync(stripItem, ct);
        await media.SaveAsync(stripItem, ct);

        var videoItem = stripItem with
        {
            Id = MediaId.New(), Kind = MediaKind.Video, ParentId = stripItem.Id, ContentType = "video/mp4", Extension = ".mp4",
            SizeBytes = new FileInfo(mp4).Length, DurationSeconds = total,
            Summary = $"The comic \"{script.Title}\" as a narrated video.", CreatedAt = DateTimeOffset.UtcNow,
        };
        await blobs.UploadFileAsync(videoItem.SourcePath, mp4, videoItem.ContentType, ct);
        await thumbnails.CreateAsync(videoItem, ct);
        await media.SaveAsync(videoItem, ct);
        return (stripItem.Id.Value, videoItem.Id.Value);
    }

    /// <summary>What the picture model is asked for: the strip's one style, every character's fixed look, and no words.</summary>
    internal static string PanelPrompt(ComicScript script, ComicPanel panel, int index)
    {
        var cast = string.Join("; ", script.Characters.Select(c => $"{c.Name}: {c.Look}"));
        return $"{script.ArtStyle}. Panel {index + 1} of {script.Panels.Count} of a comic strip, square frame. "
            + (cast.Length > 0 ? $"Characters, drawn exactly like this in every panel: {cast}. " : "")
            + $"Scene: {panel.Scene} "
            + "Keep the top third of the frame simple background, because speech bubbles will be added there later. "
            + "Absolutely no text, letters, words, signs, speech bubbles or captions anywhere in the picture.";
    }

    private static readonly Lazy<byte[]> Square = new(() =>
    {
        using var blank = new SKBitmap(32, 32);
        blank.Erase(SKColors.White);
        return ImageBytes.ToPng(blank);
    });

    private static async Task<byte[]> DrawAsync(IImageGenerationService pictures, string prompt, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return (await pictures.GenerateAsync(prompt, Square.Value, ct)).Data;
            }
            catch (Exception e) when (attempt < 2 && e is not OperationCanceledException)
            {
                // One more try: a picture that fails once often draws the second time.
            }
            catch (Exception e) when (e is not (OperationCanceledException or RunStepException))
            {
                throw new ComicException("A panel could not be drawn: the picture service turned it down twice.", e);
            }
        }
    }

    /// <summary>Each panel's spoken parts as audio files, in order: the caption by the narrator, then the lines.</summary>
    private async Task<List<string>[]> VoiceAsync(ComicScript script, string folder, CancellationToken ct)
    {
        var parts = script.Panels.Select((p, i) =>
            (p.Caption.Length > 0 ? [(Text: p.Caption, Voice: ComicScript.NarratorVoice, How: "A wry, theatrical comic-book narrator, building suspense.")] : Array.Empty<(string Text, string Voice, string How)>())
            .Concat(p.Lines.Select(l => (l.Text, script.VoiceOf(l.Speaker),
                $"You are {l.Speaker}, a cartoon character in a funny comic. Say it {(l.Delivery.Length > 0 ? l.Delivery : "naturally, with feeling")}.")))
            .Select((part, j) => (Panel: i, Index: j, part.Item1, part.Item2, part.Item3))
            .ToList()).ToList();

        var files = parts.Select(p => new List<string>(new string[p.Count])).ToArray();
        using var speaking = new SemaphoreSlim(SpeakAtOnce);
        await Task.WhenAll(parts.SelectMany(p => p).Select(async part =>
        {
            await speaking.WaitAsync(ct);
            try
            {
                files[part.Panel][part.Index] = await SpeakAsync(part.Item3, part.Item4, part.Item5, Path.Combine(folder, $"line-{part.Panel}-{part.Index}"), ct);
            }
            finally
            {
                speaking.Release();
            }
        }));

        // A panel with nothing said still holds the screen for a moment.
        for (var i = 0; i < files.Length; i++)
            if (files[i].Count == 0)
                files[i].Add(await SilenceAsync(1.5, Path.Combine(folder, $"line-{i}-quiet"), ct));
        return files;
    }

    private async Task<string> SpeakAsync(string text, string voice, string how, string stem, CancellationToken ct)
    {
        if (speech.IsConfigured)
        {
            var mp3 = stem + ".mp3";
            await File.WriteAllBytesAsync(mp3, await speech.SpeakAsync(text, voice, how, ct), ct);
            return mp3;
        }
        // No voice: as long as the words take to read.
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return await SilenceAsync(Math.Max(1.2, words / 2.7), stem, ct);
    }

    private async Task<string> SilenceAsync(double seconds, string stem, CancellationToken ct)
    {
        var wav = stem + ".wav";
        await FfmpegAsync(FFmpegArgs.ComicSilenceArgs(seconds, wav), ct);
        return wav;
    }

    /// <summary>The best library match for each panel's sound cue and for the music mood, fetched; null where none fits.</summary>
    private async Task<(string?[] Cues, string? Music)> SoundsAsync(UserId user, ComicScript script, string folder, CancellationToken ct)
    {
        var cues = new string?[script.Panels.Count];
        var library = (await sounds.LoadAllAsync(ct)).VisibleTo(user);
        var queries = script.Panels.Select(p => p.Sound).Append(script.Music).ToList();
        var matches = matcher.GetTopCandidatesBatch(library, [.. queries.Select(q => q.Length > 0 ? q : "-")], topN: 1);

        async Task<string?> FetchAsync(int i)
        {
            if (queries[i].Length == 0 || i >= matches.Count || matches[i].Count == 0)
                return null;
            var sound = matches[i][0].Sound;
            var file = Path.Combine(folder, $"sound-{i}{Path.GetExtension(sound.BlobPath)}");
            try
            {
                await blobs.DownloadToFileAsync(sound.BlobPath, file, ct);
                return file;
            }
            catch (Exception e) when (e is Azure.RequestFailedException or IOException)
            {
                LogSoundSkipped(e, queries[i]);
                return null;
            }
        }

        for (var i = 0; i < cues.Length; i++)
            cues[i] = await FetchAsync(i);
        return (cues, await FetchAsync(queries.Count - 1));
    }

    private async Task FfmpegAsync(string args, CancellationToken ct)
    {
        if (await ffmpeg.RunAsync(args, "comic", ct) != 0)
            throw new ComicException("The comic's video could not be rendered.");
    }
}

/// <summary>A failure whose message the user should see.</summary>
public sealed class ComicException(string message, Exception? inner = null) : Exception(message, inner);
