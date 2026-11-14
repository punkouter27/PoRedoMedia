using System.Globalization;
using System.Text.Json;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Movie.Library;
using PoRedoMedia.Movie.Render;
using PoRedoMedia.Movie.Script;
using PoRedoMedia.Movie.Voice;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Movies;

// Every film and AI video clip goes through Meme-ify before it is joined into the movie: meme sounds, stickers, text
// and effects at the right moments, and a spoken roast over it. It is done to the clip as it plays, voice-over and
// all, so the jokes land after what is said. The movie keeps what Meme-ify made; nothing is left in the Library.
public sealed partial class MovieStudio
{
    /// <summary>Names a meme-ified clip on disk. Change it when what Meme-ify is asked for changes, so clips are made again.</summary>
    private const string MemeVersion = "meme 3"; // 2: the roast answers the narrator; 3: the narrator stays on top of the meme sounds

    private bool CanMeme => memes?.Handles.Contains(MediaFunction.Memeify) == true;

    /// <summary>
    /// The roast is a savage stand-up comedian. It is not put over a kids'' video: those get the meme sounds, stickers,
    /// text and effects, in the mildest persona, and no spoken insults.
    /// </summary>
    private bool Roasts(Show show) => MemeRun(show, memes?.Handles ?? new HashSet<MediaFunction>(), narrated: false).Functions.Contains(MediaFunction.VideoRoast);

    /// <summary>
    /// What a clip of this series is put through, of what the server can do, and with which persona. Over a clip the
    /// narrator speaks, the roast is a heckler: it answers the narrator, in the narrator's pauses.
    /// </summary>
    internal static (MediaFunction[] Functions, Dictionary<string, string> Options) MemeRun(Show show, IReadOnlySet<MediaFunction> available, bool narrated)
    {
        var roast = show != Show.Kids && available.Contains(MediaFunction.VideoRoast);
        var options = new Dictionary<string, string>();
        if (show == Show.Kids) options[RunOptions.VideoPersona] = "sitcom";
        // Over a narrator, the narrator stays on top: the meme sounds come in under the voice-over.
        if (narrated) options[RunOptions.VideoVoiceFirst] = "1";
        if (narrated && roast) options[RunOptions.VideoRoastNarrated] = "1";
        return (roast ? [MediaFunction.Memeify, MediaFunction.VideoRoast] : [MediaFunction.Memeify], options);
    }

    /// <summary>The film a clip shows (the owner''s, or an AI video); null for a picture or a title card.</summary>
    private static string? FootageOf(Clip clip) => clip.Visual.Kind switch
    {
        VisualKind.UserVideo when clip.Visual.UserVideoPath is { } film && File.Exists(film) => film,
        VisualKind.AiVideo when clip.Visual.MediaPaths is [var video, ..] && File.Exists(video) => video,
        _ => null,
    };

    /// <summary>
    /// Where the clip''s meme-ified version is, or will be: named for everything that went into it (the film, the words
    /// over it and their pace, its sound, the series), so a change to any of them makes it again.
    /// </summary>
    private static string? MemePath(OpenMovie open, Clip clip)
    {
        if (FootageOf(clip) is not { } film) return null;
        var file = new FileInfo(film);
        var show = BeatPlan.ShowOf(open.Episode.Structure);
        var key = Files.TextHash(FormattableString.Invariant(
            $"{MemeVersion}|{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{clip.OwnSound}|{clip.NarrationRate:0.###}|{clip.Active.Dialogue}|{show}"))[..20];
        return Path.Combine(open.Folder, "memes", $"{clip.Id:N}-{key}.mp4");
    }

    private static bool Memed(OpenMovie open, Clip clip) => MemePath(open, clip) is { } path && File.Exists(path);

    /// <summary>The clips still to go through Meme-ify.</summary>
    private static List<Clip> ToMeme(OpenMovie open) => [.. open.Episode.Clips.Where(c => !c.NoMemes && FootageOf(c) is not null && !Memed(open, c))];

    /// <summary>
    /// The movie as it is rendered: each clip that has been through Meme-ify shows that version, with its sound (the
    /// voice-over, the film''s own sound, the meme sounds and the roast, all in one), and the words said in it as captions.
    /// </summary>
    private static Episode WithMemes(OpenMovie open) => open.Episode with
    {
        Clips = [.. open.Episode.Clips.Select(clip => !clip.NoMemes && MemePath(open, clip) is { } memed && File.Exists(memed)
            ? clip with
            {
                Visual = new VisualSpec(VisualKind.UserVideo, KindLocked: true, UserVideoPath: memed),
                OwnSound = true,
                RobotView = clip.RobotView && clip.Visual.Kind == VisualKind.UserVideo,
            }
            : clip)],
    };

    /// <summary>What Meme-ify costs for one clip, as the server prices it.</summary>
    private decimal MemePrice => engine.MemePrice;

    /// <summary>What meme-ifying the clips not yet done would cost, to show before rendering.</summary>
    public MovieEstimateDto MemeEstimate(OpenMovie open)
    {
        var count = CanMeme ? ToMeme(open).Count : 0;
        if (count == 0) return new("Every clip is ready.", 0, true, null);
        var show = BeatPlan.ShowOf(open.Episode.Structure);
        var dollars = count * MemePrice;
        var what = Roasts(show) ? "meme sounds, stickers, text and effects, and a spoken roast" : "meme sounds, stickers, text and effects (no roast in a kids'' video)";
        return new($"First, {Count(count, "film and AI video clip")} {(count == 1 ? "goes" : "go")} through Meme-ify: an AI watches each and adds {what}, about {SpendLog.Money(dollars)}. Clips already done are reused at no cost. Then the movie is rendered and put in your Library.",
            dollars, false, OverBudget(dollars));
    }

    /// <summary>Puts every clip not yet done through Meme-ify, one at a time. A clip that fails is joined as it is.</summary>
    /// <returns>A line for each clip that could not be done.</returns>
    private async Task<List<string>> MemeifyAsync(Job job, OpenMovie open, CancellationToken ct)
    {
        var clips = ToMeme(open);
        var failures = new List<string>();
        if (clips.Count == 0) return failures;
        // Without Meme-ify the clips are simply joined as they are: the Video step says so before, so it is not a failure.
        if (!CanMeme) return failures;
        if (OverBudget(clips.Count * MemePrice) is { } refusal)
        {
            failures.Add($"The clips were joined without memes. {refusal}");
            return failures;
        }
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            Report(job, $"Meme-ifying clip {i + 1} of {clips.Count} · {clip.Title}", (double)i / clips.Count);
            try
            {
                await MemeifyOneAsync(job, open, clip, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures.Add($"“{clip.Title}” was joined without memes: {Plain(e)}");
            }
        }
        Report(job, "Meme-ify is done", 1);
        return failures;
    }

    private async Task MemeifyOneAsync(Job job, OpenMovie open, Clip clip, CancellationToken ct)
    {
        var show = BeatPlan.ShowOf(open.Episode.Structure);
        var film = FootageOf(clip)!;
        var target = MemePath(open, clip)!;
        // The clip as it plays: its picture for as long as its sound runs, with the voice-over and the film''s own sound.
        var narration = await engine.Builder.NarrateClipAsync(clip, open.Folder, ct);
        var voiced = Files.NewScratchFile(".mp4");
        var source = new MediaItem
        {
            Owner = user,
            Id = MediaId.New(),
            Kind = MediaKind.Video,
            Status = MediaStatus.Ready,
            Origin = "Movie",
            Title = clip.Title,
            ContentType = "video/mp4",
            Extension = ".mp4",
            DurationSeconds = narration.Duration.TotalSeconds,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        MediaItem? made = null;
        var tally = AiMeter.Begin();
        try
        {
            await engine.Runner.RunAsync(VoicedClip(film, clip.Visual.Kind == VisualKind.AiVideo, narration, voiced), Path.GetDirectoryName(voiced)!, narration.Duration, null, ct);
            await blobs.UploadFileAsync(source.SourcePath, voiced, source.ContentType, ct);
            // What is said in it is known already, so Meme-ify is told and does not pay to hear it again.
            if (Known(clip, film, narration) is { Count: > 0 } speech)
                await blobs.UploadAsync(MediaAnalysisPaths.SourceSpeech(source.Id.Value), JsonSerializer.SerializeToUtf8Bytes(speech, WebJson), "application/json", ct);

            var (functions, options) = MemeRun(show, memes!.Handles, narrated: !clip.OwnSound && clip.Active.Dialogue.Trim().Length > 0);
            var run = new Run { Owner = user, Id = RunId.New(), SourceId = source.Id, Functions = functions, Options = options, Status = RunStatus.Running, CreatedAt = DateTimeOffset.UtcNow };
            var context = new RunContext(run, source,
                (item, _) => { made = item; return Task.CompletedTask; },
                line => { if (!line.StartsWith("HIT", StringComparison.Ordinal) && !line.StartsWith("TURN", StringComparison.Ordinal)) job.Detail = $"{clip.Title}: {line}"; return Task.CompletedTask; });
            await memes!.ExecuteAsync(context, ct);
            if (made is null)
                throw new InvalidOperationException("Meme-ify made nothing for this clip.");

            var scratch = Files.NewScratchFile(".mp4");
            try
            {
                await blobs.DownloadToFileAsync(made.SourcePath, scratch, ct);
                Files.EnsureFolderFor(target);
                File.Move(scratch, target, overwrite: true);
            }
            finally
            {
                File.Delete(scratch);
            }
            // The words said in it, at their times, become its captions, as the voice-over''s would have.
            JsonFile.Save(Clip.HeardFor(target), narration.Words.ToList());
            await engine.Builder.PosterAsync(target, ct);
        }
        finally
        {
            File.Delete(voiced);
            if ((tally.Snapshot().CostUsd ?? 0) is > 0 and var dollars)
                SpendLog.Add(open.Folder, "meme-ify", dollars, 1);
            // The clip and what Meme-ify made of it were working files, not the user''s: none is left in storage or the Library.
            await ForgetAsync(source.Id, null);
            if (made is not null) await ForgetAsync(made.Id, made.Id);
        }
    }

    private async Task ForgetAsync(MediaId id, MediaId? row)
    {
        try
        {
            await blobs.DeletePrefixAsync(MediaBlobPaths.Prefix(id), CancellationToken.None);
            if (row is { } saved) await media.DeleteAsync(user, saved, CancellationToken.None);
        }
        catch (Exception e) when (e is Azure.RequestFailedException or IOException)
        {
            // Left for the retention sweep, which removes old items by age.
        }
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// What is said in the clip, in lines, when it is truly known: the voice-over, or what was heard in a film that keeps
    /// its own sound. Words spread by guess across a film are not told: Meme-ify would time its jokes by them.
    /// </summary>
    internal static List<TranscriptSegmentDto>? Known(Clip clip, string film, Narration narration)
    {
        if (narration.Words.Count == 0 || (clip.OwnSound && !File.Exists(Clip.HeardFor(film)))) return null;
        var lines = new List<TranscriptSegmentDto>();
        var line = new List<WordTiming>();
        foreach (var word in narration.Words)
        {
            line.Add(word);
            if (line.Count >= 12 || word.Text.EndsWith('.') || word.Text.EndsWith('!') || word.Text.EndsWith('?'))
                Flush();
        }
        Flush();
        return lines;

        void Flush()
        {
            if (line.Count == 0) return;
            lines.Add(new(line[0].Start.TotalSeconds, (line[^1].Start + line[^1].Duration).TotalSeconds, string.Join(' ', line.Select(w => w.Text))));
            line.Clear();
        }
    }

    /// <summary>
    /// The clip''s picture for exactly as long as its sound: an AI video repeated, a film held on its last frame, at no
    /// more than 1280 wide (Meme-ify''s own size) and 30 frames a second.
    /// </summary>
    internal static IReadOnlyList<string> VoicedClip(string film, bool loop, Narration narration, string output)
    {
        var seconds = narration.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return
        [
            "-y", .. loop ? new[] { "-stream_loop", "-1" } : [], "-i", film, "-i", narration.AudioPath,
            "-filter_complex", $"[0:v]scale='trunc(min(1280,iw)/2)*2':-2,setsar=1,fps=30,tpad=stop_mode=clone:stop_duration={seconds}[v]",
            "-map", "[v]", "-map", "1:a", "-t", seconds,
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", output,
        ];
    }
}
