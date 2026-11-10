using System.Collections.Concurrent;
using System.Globalization;
using PoRedoMedia.Movie.Library;
using PoRedoMedia.Movie.Pictures;
using PoRedoMedia.Movie.Render;
using PoRedoMedia.Movie.Script;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Movies;

// The series' brand kit, watching a movie before it is finished, and the finished video with everything that goes with it.
public sealed partial class MovieStudio
{
    private static readonly string[] MusicTypes = [".mp3", ".wav", ".m4a", ".flac", ".ogg"];
    private static readonly string[] PictureTypes = [".png", ".jpg", ".jpeg", ".webp"];

    // ---- The brand kit ----

    private Brand BrandOf(Show show) => new(SeriesKit.Load(KitFolder(show), show), KitFolder(show));

    private Brand BrandOf(OpenMovie open) => BrandOf(BeatPlan.ShowOf(open.Episode.Structure));

    public MovieKitDto Kit(Show show)
    {
        var (folder, kit) = (KitFolder(show), SeriesKit.Load(KitFolder(show), show));
        string? Url(string? name) => SeriesKit.PathOf(folder, name) is { } path
            ? $"/api/movies/kit/{show}/files/{Uri.EscapeDataString(name!)}?v={File.GetLastWriteTimeUtc(path).Ticks:x}" : null;
        return new MovieKitDto(show.ToString(), kit.ChannelName, kit.Tagline, kit.OutroSeconds, kit.Grain, kit.Structure.ToString(), kit.Look.ToString(), kit.Layout.ToString(),
            kit.Captions.Preset.ToString(), kit.Captions.AccentColor, kit.Captions.FontSize, kit.Captions.StrokeWidth,
            Url(kit.Logo), Url(kit.Mascot), Url(kit.Sting), Url(kit.Music), SeriesKit.PathOf(folder, kit.Music) is null ? null : kit.Music,
            SeriesKit.PathOf(folder, kit.Sting) is null ? null : kit.Sting,
            ProjectStore.NextNumber(Episodes, show), File.Exists(Path.Combine(Host, "sheet.png")));
    }

    public string? KitFile(Show show, string name) => SeriesKit.PathOf(KitFolder(show), name);

    /// <returns>Why the kit could not be saved, or null.</returns>
    public string? SaveKit(Show show, MovieKitUpdate update)
    {
        if (!AssCaptions.IsHexColour(update.Accent)) return "The accent colour is typed as # and six letters or digits, such as #22CC88.";
        if (!Enum.TryParse<BeatStructure>(update.Structure, out var structure) || !BeatPlan.Structures(show).Contains(structure)) return "That shape does not belong to this series.";
        if (!Enum.TryParse<Look>(update.Look, out var look) || !Enum.TryParse<IntroLayout>(update.Layout, out var layout) || !Enum.TryParse<CaptionPreset>(update.CaptionPreset, out var preset))
            return "That choice is not known.";
        var kit = SeriesKit.Load(KitFolder(show), show) with
        {
            ChannelName = Cut(update.ChannelName, 60),
            Tagline = Cut(update.Tagline.Trim(), 80),
            OutroSeconds = Math.Clamp(update.OutroSeconds, SeriesKit.MinOutroSeconds, SeriesKit.MaxOutroSeconds),
            Grain = update.Grain,
            Structure = structure,
            Look = look,
            Layout = layout,
            Captions = new CaptionStyle(preset, Math.Clamp(update.FontSize, 32, 96), update.Accent.ToUpperInvariant(), Math.Clamp(update.Stroke, 0, 10)),
        };
        return StoreKit(show, kit);
    }

    private static string Cut(string text, int most) => text.Length > most ? text[..most] : text;

    private string? StoreKit(Show show, SeriesKit kit)
    {
        try
        {
            Directory.CreateDirectory(KitFolder(show));
            kit.Save(KitFolder(show));
            return null;
        }
        catch (IOException e)
        {
            return $"The series settings could not be saved: {e.Message}";
        }
    }

    /// <summary>Gives the kit a picture or a sound of the user's own, or with a null <paramref name="file"/> goes back to the made-up one.</summary>
    /// <param name="role">"logo", "mascot", "sting" or "music".</param>
    public string? SetKitFile(Show show, string role, string? file)
    {
        if (role is not ("logo" or "mascot" or "sting" or "music")) return "That is not part of the kit.";
        var name = file is null ? null : SeriesKit.Import(KitFolder(show), file, role);
        var kit = SeriesKit.Load(KitFolder(show), show);
        return StoreKit(show, role switch
        {
            "logo" => kit with { Logo = name },
            "mascot" => kit with { Mascot = name },
            "sting" => kit with { Sting = name },
            _ => kit with { Music = name },
        });
    }

    /// <summary>Whether a file of this name may be the kit's (or a movie's) picture or sound.</summary>
    public static string? Accepts(string role, string fileName) => (role, Path.GetExtension(fileName).ToLowerInvariant()) switch
    {
        ("logo" or "mascot" or "host", var type) when PictureTypes.Contains(type) => null,
        ("logo" or "mascot" or "host", _) => "Choose a PNG, JPG or WEBP picture.",
        (_, var type) when MusicTypes.Contains(type) => null,
        _ => "Music must be an MP3, WAV, M4A, FLAC or OGG file.",
    };

    /// <summary>One still of the intro or outro as the kit stands, drawn in a moment and free.</summary>
    /// <param name="number">The episode number to show; null for the next in the series.</param>
    public async Task<string> BookendStillAsync(Show show, bool intro, int? number, CancellationToken ct) =>
        Temp(await engine.Builder.BookendStillAsync(intro, BrandOf(show), NumberOr(show, number), ct));

    private int NumberOr(Show show, int? number) => number is >= 1 and <= 999 ? number.Value : ProjectStore.NextNumber(Episodes, show);

    /// <summary>The intro or outro in full, moving and with its sound. Takes a few seconds; free.</summary>
    public async Task<string> BookendVideoAsync(Show show, bool intro, int? number, CancellationToken ct) =>
        Temp(intro ? await engine.Builder.PreviewIntroAsync(BrandOf(show), NumberOr(show, number), ct) : await engine.Builder.PreviewOutroAsync(BrandOf(show), ct));

    public async Task<string> BookendSoundAsync(Show show, bool intro, CancellationToken ct) =>
        Temp(await engine.Builder.BookendSoundAsync(intro, BrandOf(show), ct));

    /// <summary>What three candidate designs cost, to show before they are agreed to.</summary>
    public MovieEstimateDto CandidatesEstimate(Show show, bool logos)
    {
        var dollars = 3 * CostEstimate.PriceOf(engine.ImageModel, Quality);
        var summary = logos
            ? $"Draws three logo designs for {SeriesKit.Load(KitFolder(show), show).ChannelName}, in the series colour. You pick one, or none. Asking again with nothing changed shows the same three, free."
            : "Draws three designs for the host, the character who appears in every episode. You lock one, or none. Asking again with nothing changed shows the same three, free.";
        return new(summary, dollars, false, OverBudget(dollars ?? 0));
    }

    /// <summary>Draws three logos, or three host designs, to choose between. Paid for like any three pictures.</summary>
    public async Task<(IReadOnlyList<string> Urls, string? Error)> CandidatesAsync(Show show, bool logos, CancellationToken ct)
    {
        if (engine.VisualsFor(Host, Host, Quality, Writer) is not { } visuals) return ([], "No picture service is configured on this server.");
        var kit = SeriesKit.Load(KitFolder(show), show);
        if (logos && kit.ChannelName.Trim().Length == 0) return ([], "Name the channel first: the logos are drawn for it.");
        if (OverBudget(3 * (CostEstimate.PriceOf(engine.ImageModel, Quality) ?? 0)) is { } refusal) return ([], refusal);
        SpendIn(Host);
        try
        {
            var made = logos ? await visuals.CandidateLogosAsync(kit.ChannelName, kit.Captions.AccentColor, 3, ct) : await visuals.CandidateSheetsAsync(3, ct);
            return ([.. made.Select(Temp)], null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ([], Plain(e));
        }
    }

    /// <summary>Makes a picture the host for every movie from now on: one of the candidates, or the user's own.</summary>
    public string? LockHost(string path)
    {
        engine.VisualsFor(Host, Host, Quality, Writer)?.LockSheet(path);
        if (!engine.PicturesAvailable)
        {
            Directory.CreateDirectory(Host);
            File.Copy(path, Path.Combine(Host, "sheet.png"), overwrite: true);
        }
        return null;
    }

    public string? UseHostAsMascot(Show show) =>
        File.Exists(Path.Combine(Host, "sheet.png")) ? SetKitFile(show, "mascot", Path.Combine(Host, "sheet.png")) : "Choose a host design first.";

    // ---- Watching before it is finished ----

    /// <summary>Gives the movie outro music of its own, in place of the series'.</summary>
    public string? SetMusic(OpenMovie open, string file, string name)
    {
        var copy = Import(file, open.Folder, "music-" + Path.GetFileName(name));
        return Edit(open, e => EpisodeEditor.SetMusic(e, copy));
    }

    /// <summary>A quick, small version of the whole movie to watch, with no captions burned in: they are drawn live over it.</summary>
    public string? BuildPreview(OpenMovie open)
    {
        if (!engine.FfmpegAvailable) return "FFmpeg is needed to build the preview, and it was not found on this server.";
        return Start("Building the preview", open.Id, open.Folder, async (job, ct) =>
        {
            open.Preview = null;
            open.Preview = await engine.Builder.PreviewAsync(WithMemes(open), open.Folder, BrandOf(open), Rendering(job), ct);
        }, "Preview ready.", "preview");
    }

    /// <summary>
    /// One clip on its own, small and with its sound: as Meme-ify made it, once it has been through. Its voice is recorded
    /// first when it has not been.
    /// </summary>
    public async Task<(string? Url, string? Error)> ClipPreviewAsync(OpenMovie open, Guid clipId, CancellationToken ct)
    {
        if (!engine.FfmpegAvailable) return (null, "FFmpeg is needed to play a clip, and it was not found on this server.");
        if (IsWorking) return (null, "Wait for the job under way to finish, then press play again.");
        var episode = WithMemes(open);
        var index = episode.Clips.ToList().FindIndex(c => c.Id == clipId);
        if (index < 0) return (null, "This clip is no longer in the movie.");
        SpendIn(open.Folder);
        try
        {
            return (Temp(await engine.Builder.ClipPreviewAsync(episode, open.Folder, index, BrandOf(open), ct)), null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (null, Plain(e));
        }
    }

    // ---- The finished video ----

    /// <summary>Renders the finished video, with its chapter list, subtitles, thumbnail and upload notes beside it.</summary>
    public async Task<string?> RenderAsync(OpenMovie open, string? preset)
    {
        if (!engine.FfmpegAvailable) return "FFmpeg is needed to render the video, and it was not found on this server.";
        var size = Preset(preset);
        return await StartRenderAsync($"Rendering the finished video ({size.Height}p, {size.Fps} fps)", open.Id, open.Folder, (job, ct) => RenderMasterAsync(job, open, size, ct), "video");
    }

    /// <summary>
    /// Every voice is recorded and the movie held to its length; every film and AI video clip then goes through Meme-ify;
    /// then the movie is rendered, its upload notes written, and it is put in the Library.
    /// </summary>
    private async Task RenderMasterAsync(Job job, OpenMovie open, ExportPreset preset, CancellationToken ct)
    {
        SpendIn(open.Folder);
        var (from, span) = job.Phase;
        job.Phase = (from, span * 0.1);
        await NarrateEveryClipAsync(job, open, ct);
        CheckLength(open);
        job.Phase = (from + span * 0.1, span * 0.4);
        var memeFailures = await MemeifyAsync(job, open, ct);
        job.Phase = (from + span * 0.5, span * 0.45);
        var (episode, brand) = (open.Episode, BrandOf(open));
        var video = await engine.Builder.ExportAsync(WithMemes(open), open.Folder, preset, episode.Captions, brand, Rendering(job), ct);
        job.Made = true;
        job.Phase = (from + span * 0.95, span * 0.05);
        open.VideoLink = null;
        var notesWritten = true;
        try
        {
            Report(job, "Writing the title, description and tags to upload with it", 1);
            var notes = (await Writer.WritePublishNotesAsync(episode, ct)).InSeries(brand.Kit.ChannelName, episode.Number);
            var name = Path.ChangeExtension(video, null);
            await File.WriteAllTextAsync(name + ".description.txt", PublishPack.Description(notes), ct);
            JsonFile.Save(name + ".notes.json", notes);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            notesWritten = false;
            job.Error = "The video is finished, but its upload description could not be written. " + Plain(e);
        }
        Report(job, "Putting it in your Library", 0.5);
        var (_, libraryProblem) = await SaveToLibraryAsync(open, ct);
        job.Message = $"Saved {Path.GetFileName(video)}{(libraryProblem is null ? " and put it in your Library" : "")}, with its chapter list, subtitles{(notesWritten ? ", thumbnail and upload description" : " and thumbnail")} beside it."
            + (brand.Kit.ChannelName.Trim().Length == 0 ? " It has no intro or outro: give the channel a name on the Template step to add them." : "");
        var problems = new[] { job.Error, libraryProblem }.Concat(memeFailures).OfType<string>().ToList();
        job.Error = problems.Count > 0 ? string.Join(" ", problems) : null;
    }

    /// <summary>
    /// Holds a movie to its length before anything is paid for: every clip under a minute, and the whole movie up to ten.
    /// Measured from the recordings, which are made first.
    /// </summary>
    private void CheckLength(OpenMovie open)
    {
        var clips = open.Episode.Clips;
        // Only the intro and outro are not filmed: a clip with no video of the user's is a gap in the movie.
        for (var i = 0; i < clips.Count; i++)
            if (clips[i].Visual is not { Kind: VisualKind.UserVideo, UserVideoPath: { } film } || !File.Exists(film))
                throw new InvalidOperationException($"Clip {i + 1}, “{clips[i].Title}”, has no video of yours. Put one of your videos on it from its Picture tab, or delete it.");
        var lengths = clips.Select(c => engine.Builder.MeasuredDuration(c, open.Folder) ?? Durations.Estimate(c.Active.Dialogue)).ToList();
        for (var i = 0; i < clips.Count; i++)
            if (lengths[i] > Durations.MaxClip)
                throw new InvalidOperationException($"Clip {i + 1}, “{clips[i].Title}”, runs {lengths[i]:m\\:ss}; each clip must be under a minute. Cut some of its words, or use a shorter film.");
        if (TimeSpan.FromTicks(lengths.Sum(l => l.Ticks)) is var total && total > Durations.MaxMovie)
            throw new InvalidOperationException($"The movie runs {total:m\\:ss}; a movie can be up to 10 minutes. Take out a clip or shorten some.");
    }

    /// <summary>A few thumbnails to choose between, made from the movie's own pictures. Free.</summary>
    public string? MakeThumbnails(OpenMovie open)
    {
        if (!engine.FfmpegAvailable) return "FFmpeg is needed to make thumbnails, and it was not found on this server.";
        return Start("Making thumbnails", open.Id, open.Folder, async (_, ct) =>
            open.ThumbnailChoices = await engine.Builder.ThumbnailsAsync(open.Episode, BrandOf(open), ct), "Click the thumbnail you like best to use it.");
    }

    public string? PickThumbnail(OpenMovie open, int index)
    {
        if (index < 0 || index >= open.ThumbnailChoices.Count || !File.Exists(open.ThumbnailChoices[index])) return "That thumbnail is no longer there. Make them again.";
        var chosen = Path.Combine(open.Folder, "export", EpisodeBuilder.ThumbnailName);
        Files.EnsureFolderFor(chosen);
        File.Copy(open.ThumbnailChoices[index], chosen, overwrite: true);
        return null;
    }

    // ---- Teasers ----

    private static Teaser ToTeaser(MovieTeaserDto dto, Episode episode) => new(dto.Hook,
        [.. dto.Moments.Where(m => m.Clip >= 1 && m.Clip <= episode.Clips.Count)
            .Select(m => new TeaserMoment(episode.Clips[m.Clip - 1].Id, TimeSpan.FromSeconds(double.IsFinite(m.Start) ? Math.Max(0, m.Start) : 0), TimeSpan.FromSeconds(m.Length)))],
        dto.Fill);

    private static MovieTeaserDto ToDto(Teaser teaser, Episode episode, bool keep = true) => new(teaser.Hook, keep, teaser.Fill,
        [.. teaser.Moments.Select(m => new MovieTeaserMomentDto(episode.Clips.ToList().FindIndex(c => c.Id == m.ClipId) + 1, m.Start.TotalSeconds, m.Length.TotalSeconds))]);

    /// <summary>Three ten-second upright cuts of the movie, each a hook over one to three moments.</summary>
    public string? SuggestTeasers(OpenMovie open)
    {
        if (!engine.FfmpegAvailable) return "FFmpeg is needed to cut teasers, and it was not found on this server.";
        return Start("Suggesting teasers", open.Id, open.Folder, async (job, ct) =>
        {
            var episode = WithMemes(open);
            var lengths = await engine.Builder.ClipLengthsAsync(episode, open.Folder, ct);
            if (lengths.Values.Sum(l => l.TotalSeconds) < Teasers.Target.TotalSeconds)
                throw new InvalidOperationException("The movie runs under ten seconds, so there is nothing to cut a teaser from.");
            Report(job, "Choosing the moments", 0.5);
            var teasers = await Writer.SuggestTeasersAsync(episode, lengths, ct);
            open.Teasers = [.. teasers.Select(t => ToDto(t, episode))];
            job.Message = teasers.Count > 0
                ? $"{Count(teasers.Count, "teaser")} suggested. Change a hook, untick any you do not want, then render."
                : "No teaser could be cut from this movie.";
        });
    }

    /// <summary>One teaser as it stands, small and quick, to watch before rendering it. Free.</summary>
    public async Task<(string? Url, string? Error)> TeaserDraftAsync(OpenMovie open, int index, CancellationToken ct)
    {
        if (index < 0 || index >= open.Teasers.Count) return (null, "That teaser is no longer there.");
        if (IsWorking) return (null, "Wait for the job under way to finish, then press play again.");
        try
        {
            var episode = WithMemes(open);
            var lengths = await engine.Builder.ClipLengthsAsync(episode, open.Folder, ct);
            if (Teasers.Fit([ToTeaser(open.Teasers[index], episode)], lengths) is not [var teaser])
                return (null, "This teaser does not fit in ten seconds of its clips. Move a moment to a longer clip.");
            return (Temp(await engine.Builder.TeaserDraftAsync(episode, open.Folder, teaser, episode.Captions, BrandOf(open), ct)), null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (null, Plain(e));
        }
    }

    /// <summary>Builds each kept teaser as a 1080 by 1920 video, from the clips already made. Nothing new is paid for.</summary>
    public string? RenderTeasers(OpenMovie open)
    {
        var kept = open.Teasers.Where(t => t.Keep).ToList();
        if (kept.Count == 0) return "Tick at least one teaser to keep.";
        if (!engine.FfmpegAvailable) return "FFmpeg is needed to render teasers, and it was not found on this server.";
        return Start("Rendering the teasers", open.Id, open.Folder, async (job, ct) =>
        {
            var episode = WithMemes(open);
            var lengths = await engine.Builder.ClipLengthsAsync(episode, open.Folder, ct);
            var teasers = Teasers.Fit(kept.Select(t => ToTeaser(t, episode)), lengths);
            if (teasers.Count == 0)
                throw new InvalidOperationException("None of the teasers kept fits in ten seconds of its clips. Move a moment to a longer clip, or suggest them again.");
            var folder = await engine.Builder.ExportTeasersAsync(episode, open.Folder, teasers, episode.Captions, BrandOf(open), Rendering(job), ct);
            JsonFile.Save(Path.Combine(folder, "hooks.json"), teasers.Select(t => t.Hook).ToList());
            job.Message = $"Rendered {Count(teasers.Count, "teaser")}."
                + (teasers.Count < kept.Count ? $" {Count(kept.Count - teasers.Count, "teaser")} did not fit in ten seconds and was left out." : "");
        });
    }

    private static List<string> TeaserFiles(string folder)
    {
        var teasers = Path.Combine(folder, "export", "teasers");
        if (!Directory.Exists(teasers)) return [];
        var files = Directory.GetFiles(teasers, "*.mp4").Order(StringComparer.OrdinalIgnoreCase).ToList();
        var hooks = JsonFile.Load<List<string>>(Path.Combine(teasers, "hooks.json"), []);
        // An earlier render may have left more files than the last one made: only the last one's count.
        return hooks.Count == 0 ? files : [.. files.Where(f => Enumerable.Range(1, hooks.Count).Any(n => f.EndsWith($"-teaser-{n}.mp4", StringComparison.OrdinalIgnoreCase)))];
    }

    // ---- Publishing ----

    /// <summary>
    /// Puts the finished video in the user's Library, beside everything else they have made, where it can be
    /// downloaded, shared by link or used as the source of a run.
    /// </summary>
    /// <remarks>
    /// The copy made from the movie's last render is replaced, unless it has been shared or pinned: then the user has
    /// done something with it, and it is kept beside the new one.
    /// </remarks>
    public async Task<(Guid? Id, string? Error)> SaveToLibraryAsync(OpenMovie open, CancellationToken ct)
    {
        if (EpisodeBuilder.LatestMaster(open.Folder) is not { } master) return (null, "Render the finished video first.");
        if (LoadLibraryCopy(open.Folder) is { } earlier && earlier.Master == Path.GetFileName(master) && File.GetLastWriteTimeUtc(master) <= earlier.Saved
            && await media.GetAsync(user, MediaId.From(earlier.Id), ct) is not null)
            return (open.LibraryId = earlier.Id, null); // already there, as it is now
        var item = new MediaItem
        {
            Owner = user,
            Id = MediaId.New(),
            Kind = MediaKind.Video,
            Status = MediaStatus.Ready,
            Origin = "Movie",
            Title = open.Episode.Title,
            ContentType = "video/mp4",
            Extension = ".mp4",
            SizeBytes = new FileInfo(master).Length,
            DurationSeconds = (await engine.Builder.ProbeDurationAsync(master, ct)).TotalSeconds,
            Text = File.Exists(Path.ChangeExtension(master, ".description.txt")) ? await File.ReadAllTextAsync(Path.ChangeExtension(master, ".description.txt"), ct) : null,
            Summary = "A movie made in Create Movie.",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        try
        {
            await blobs.UploadFileAsync(item.SourcePath, master, item.ContentType, ct);
            await thumbnails.CreateAsync(item, ct);
            // The row goes last, so a failure above leaves no Library item pointing at missing bytes.
            await media.SaveAsync(item, ct);
        }
        catch (Exception e) when (e is Azure.RequestFailedException or IOException)
        {
            return (null, "The video is finished, but it could not be put in the Library: storage did not answer. Add it again from the Video step.");
        }
        if (LoadLibraryCopy(open.Folder) is { } replaced && await media.GetAsync(user, MediaId.From(replaced.Id), ct) is { Pinned: false, ShareToken: null } old)
        {
            await blobs.DeletePrefixAsync(MediaBlobPaths.Prefix(old.Id), ct);
            await media.DeleteAsync(user, old.Id, ct);
        }
        MovieJson.Save(LibraryFile(open.Folder), new LibraryCopy(item.Id.Value, Path.GetFileName(master), DateTime.UtcNow));
        open.LibraryId = item.Id.Value;
        return (item.Id.Value, null);
    }

    /// <summary>Which Library item holds the movie, made from which render, and when.</summary>
    private sealed record LibraryCopy(Guid Id, string Master, DateTime Saved);

    private static string LibraryFile(string folder) => Path.Combine(folder, "export", "library.json");

    private static LibraryCopy? LoadLibraryCopy(string folder) => MovieJson.Load<LibraryCopy?>(LibraryFile(folder), null);

    /// <summary>The Library item made from the movie's latest render, when there is one.</summary>
    private static Guid? LibraryIdOf(string folder) =>
        LoadLibraryCopy(folder) is { } copy && EpisodeBuilder.LatestMaster(folder) is { } master && copy.Master == Path.GetFileName(master) ? copy.Id : null;

    private sealed record PendingUpload(string MovieId, MovieUploadRequest Request, string Verifier, string Redirect);

    private readonly ConcurrentDictionary<string, PendingUpload> _signIns = new();

    /// <summary>
    /// Begins an upload to YouTube: the user signs in with Google in a new tab, and Google sends that tab back here
    /// with a one-time code (<see cref="CompleteUpload"/>). The sign-in is used for this upload alone.
    /// </summary>
    /// <returns>The Google page to open, or why the upload cannot go ahead.</returns>
    public (string? Url, string? Error) BeginUpload(OpenMovie open, MovieUploadRequest request, string redirect)
    {
        if (engine.YouTube() is not { } youTube) return (null, "Uploading to YouTube is not set up on this server.");
        if (IsWorking) return (null, Busy);
        if (request.Shorts ? TeaserFiles(open.Folder).Count == 0 : EpisodeBuilder.LatestMaster(open.Folder) is null)
            return (null, request.Shorts ? "There are no rendered teasers to upload. Suggest teasers, then render them." : "Render the finished video first.");
        var (verifier, challenge) = YouTube.NewProof();
        var state = YouTube.NewState();
        _signIns.Clear(); // one sign-in at a time: starting another forgets the last
        _signIns[state] = new PendingUpload(open.Id, request, verifier, redirect);
        return (youTube.SignInPage(redirect, state, challenge).AbsoluteUri, null);
    }

    /// <returns>What to tell the user in the tab Google sent back.</returns>
    public string CompleteUpload(string? state, string? code)
    {
        if (state is null || !_signIns.TryRemove(state, out var pending) || code is null || engine.YouTube() is not { } youTube || Open(pending.MovieId) is not { } open)
            return "Sign-in was not finished. You can close this tab and go back to PoRedoMedia.";
        var problem = Start(pending.Request.Shorts ? "Uploading the teasers to YouTube" : "Uploading to YouTube", open.Id, open.Folder, async (job, ct) =>
        {
            Report(job, "Signing in to Google", 0.01);
            var token = await youTube.TokenAsync(code, pending.Redirect, pending.Verifier, ct);
            var show = BeatPlan.ShowOf(open.Episode.Structure);
            var privacy = Enum.TryParse<Privacy>(pending.Request.Privacy, out var chosen) ? chosen : Privacy.Private;
            var seen = pending.Request.PublishAt is { } at ? $"private until {at.ToLocalTime():d MMM yyyy, HH:mm}, then public" : privacy.ToString().ToLowerInvariant();
            var tags = pending.Request.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            UploadDetails Details(string title, string description) => new(title, description, tags, privacy, pending.Request.PublishAt)
            {
                MadeForKids = show == Show.Kids,
                Category = show == Show.Robot ? "28" : "22",
            };

            if (!pending.Request.Shorts)
            {
                var video = EpisodeBuilder.LatestMaster(open.Folder) ?? throw new InvalidOperationException("The finished video is not in the export folder any more. Render it again.");
                var title = pending.Request.Title.Trim() is { Length: > 0 } typed ? typed : open.Episode.Title;
                var id = await youTube.UploadAsync(token, video, Details(title, pending.Request.Description), new Sink<double>(p => Report(job, "Sending the video", 0.02 + 0.95 * p)), ct);
                open.VideoLink = $"https://youtu.be/{id}";
                var thumbnail = Path.Combine(open.Folder, "export", EpisodeBuilder.ThumbnailName);
                if (File.Exists(thumbnail))
                    try
                    {
                        await youTube.SetThumbnailAsync(token, id, thumbnail, ct);
                    }
                    catch (InvalidOperationException e)
                    {
                        job.Error = $"The video is up, but its thumbnail was not set. {e.Message}";
                    }
                job.Message = $"Uploaded, {seen}: {open.VideoLink}";
                return;
            }

            var files = TeaserFiles(open.Folder);
            var hooks = JsonFile.Load<List<string>>(Path.Combine(open.Folder, "export", "teasers", "hooks.json"), []);
            var links = new List<string>();
            foreach (var (file, i) in files.Select((f, i) => (f, i)))
            {
                var hook = i < hooks.Count ? hooks[i] : open.Episode.Title;
                var id = await youTube.UploadAsync(token, file, Details($"{hook} #Shorts", $"{open.Episode.Title}\n\nThe whole video is on the channel.\n\n#Shorts"),
                    new Sink<double>(p => Report(job, $"Sending teaser {i + 1} of {files.Count}", (i + p) / files.Count)), ct);
                links.Add($"https://youtube.com/shorts/{id}");
            }
            open.VideoLink = string.Join('\n', links);
            job.Message = $"Uploaded {Count(links.Count, "Short")}, {seen}.";
        });
        return problem ?? "Signed in. The upload has started; you can close this tab and go back to PoRedoMedia.";
    }

    // ---- What the browser is shown of a movie ----

    public MovieDto ToDto(OpenMovie open)
    {
        var episode = open.Episode;
        var folder = open.Folder;
        var show = BeatPlan.ShowOf(episode.Structure);
        var clips = episode.Clips.Select((clip, i) =>
        {
            var measured = engine.Builder.MeasuredDuration(clip, folder);
            var seconds = (measured ?? Durations.Estimate(clip.Active.Dialogue)).TotalSeconds;
            var film = clip.Visual.Kind == VisualKind.UserVideo;
            var kind = film ? "My video" : Visuals.Kinds.FirstOrDefault(k => k.Kind == clip.Visual.Kind)?.Label ?? "Title card";
            var sound = film ? clip.OwnSound ? " · my sound" : " · voice + my sound" : clip.HostVisible ? "" : show == Show.Robot ? " · voice only" : "";
            var memed = !clip.NoMemes && Memed(open, clip);
            var summary = $"{(clip.ActiveTier == Tier.B ? "" : $"{clip.ActiveTier} · ")}{kind} · {(measured is null ? "about " : "")}{seconds:0} s{sound}";
            open.Notes.TryGetValue(clip.Id, out var review);
            var warnings = new List<string>();
            if (clip.Visual.Stale) warnings.Add("The picture is out of date: the words or what it shows have changed since it was drawn. Generate it again on the Picture tab.");
            if (measured is { } recorded && recorded > Durations.MaxClip)
                warnings.Add($"Longer than a minute ({recorded:m\\:ss}). Each clip must be under a minute, or the movie is not rendered: cut some of its words.");
            else if (!film && Durations.Warning(clip) is { } length) // a film's clip runs as long as the film
                warnings.Add($"{length}. Add or cut words on the Words tab so the clip runs 15 to 60 seconds.");
            if (review is not null) warnings.Add($"Editor: {review}");
            return new MovieClipDto(clip.Id, i + 1, clip.Title, clip.Beat, clip.ActiveTier.ToString(), [.. clip.Scripts.Keys.Order().Select(t => t.ToString())],
                clip.Active.Dialogue, clip.Active.VisualPrompt, clip.Visual.Kind.ToString(), kind, clip.Visual.KindLocked && !film, clip.HostVisible, film, clip.OwnSound, clip.RobotView,
                clip.Visual.Stale, clip.Draft, seconds, measured is not null, summary, [.. warnings], review,
                Link(open, clip.Picture()), film ? Link(open, clip.Visual.UserVideoPath) : null,
                [.. (clip.Visual.EarlierTakes ?? []).Select(take => Link(open, Clip.PictureOf(take)) ?? "")], clip.Visual.MediaPaths is { Count: > 0 }, memed, FootageOf(clip) is not null, !clip.NoMemes);
        }).ToArray();

        var runtime = clips.Sum(c => c.Seconds);
        var spent = SpendLog.Total(folder);
        var meta = string.Join(" · ", new[]
        {
            Count(clips.Length, "clip"),
            $"runtime {TimeSpan.FromSeconds(runtime):m\\:ss}{(TimeSpan.FromSeconds(runtime) > Durations.MaxMovie ? ", over the 10 minute limit" : Durations.RuntimeWarning(TimeSpan.FromSeconds(runtime)) is { } off && show != Show.General ? $", {off}" : "")}",
            spent >= 0.005m ? $"spent about {SpendLog.Money(spent)}" : null,
            SpendLog.Units(folder, SpendLog.ScriptTokens) is > 0 and var tokens ? $"{tokens:N0} script tokens" : null,
            SpendLog.Units(folder, SpendLog.VoiceCharacters) is > 0 and var characters ? $"{characters:N0} voice characters" : null,
        }.OfType<string>());

        MoviePreviewDto? preview = null;
        if (open.Preview is { } built && Link(open, built.VideoPath) is { } url)
        {
            var words = built.Segments.SelectMany(s => s.Words.Select(w => new MovieWordDto(w.Text, (s.Offset + w.Start).TotalSeconds, w.Duration.TotalSeconds))).ToArray();
            var marks = built.Marks.Select(m => new MovieMarkDto(m.Title, m.Start.TotalSeconds, m.Bookend)).ToArray();
            preview = new(url, marks, words, 0);
        }

        var toMake = engine.PicturesAvailable ? CostEstimate.For(episode, engine.ImageModel, Quality).ClipIds.Count : 0;
        var spendLines = SpendLog.Breakdown(folder).Select(line => $"{line.What} · {line.Units:N0} · {SpendLog.Money(line.Dollars)}").ToArray();
        return new MovieDto(open.Id, episode.Title, episode.Topic, episode.Number, show.ToString(), episode.Structure.ToString(), episode.Look.ToString(),
            new(episode.Captions.Preset.ToString(), episode.Captions.AccentColor, episode.Captions.FontSize, episode.Captions.StrokeWidth),
            clips, runtime, meta, open.History.CanUndo, open.History.CanRedo,
            episode.MusicPath is { } music && File.Exists(music) ? Path.GetFileName(music)["music-".Length..] : null,
            toMake, episode.Clips.Any(c => c.Draft), OffSeriesLook(open), preview, PublishOf(open), [.. open.Teasers], spendLines);
    }

    private MoviePublishDto PublishOf(OpenMovie open)
    {
        var episode = open.Episode;
        var master = EpisodeBuilder.LatestMaster(open.Folder);
        var name = master is null ? null : Path.ChangeExtension(master, null);
        var chapters = name is not null && File.Exists(name + ".chapters.txt") ? File.ReadAllText(name + ".chapters.txt") : "";
        var notes = name is null ? null : JsonFile.Load<PublishNotes?>(name + ".notes.json", null);
        var title = notes is { Titles: [var first, ..] } ? first
            : new PublishNotes([episode.Title], "", [], []).InSeries(BrandOf(open).Kit.ChannelName, episode.Number).Titles[0];
        var description = string.Join("\n\n", new[] { notes?.Description, chapters.Trim(), notes is null ? null : string.Join(' ', notes.Hashtags) }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var thumbnail = Path.Combine(open.Folder, "export", EpisodeBuilder.ThumbnailName);
        var size = master is null ? null : string.Create(CultureInfo.InvariantCulture, $"{new FileInfo(master).Length / 1_048_576.0:0.0} MB");
        return new MoviePublishDto(Link(open, master), master is null ? null : Path.GetFileName(master), size, title, description, string.Join(", ", notes?.Tags ?? []), chapters,
            Link(open, thumbnail), [.. open.ThumbnailChoices.Where(File.Exists).Select(Temp)], [.. TeaserFiles(open.Folder).Select(f => Link(open, f)!)], open.VideoLink, open.LibraryId);
    }
}
