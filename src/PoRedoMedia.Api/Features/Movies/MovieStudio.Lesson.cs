using PoRedoMedia.Movie.Library;
using PoRedoMedia.Movie.Pictures;
using PoRedoMedia.Movie.Render;
using PoRedoMedia.Movie.Script;
using PoRedoMedia.Movie.Voice;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Movies;

// Starting a movie: the owner's films, the script written around them, and the whole video made from them in one go.
public sealed partial class MovieStudio
{
    public const int MostFilms = 20;

    /// <summary>
    /// Every movie is the series' intro, then the user's own films, then the outro: nothing drawn or generated goes
    /// between them.
    /// </summary>
    public const string OnlyFilms = "Add your films first: a movie is your own videos, between the series' intro and outro.";

    /// <summary>The shortest film that can be listed. One under five seconds is too short to narrate over, so it keeps its own sound.</summary>
    private static readonly TimeSpan MinFilm = TimeSpan.FromSeconds(2);

    private const int VoicesAtOnce = 4;

    /// <summary>
    /// One of the user's Library videos as a file on this server, fetched from storage the first time. FFmpeg and the
    /// models that watch a film need it beside them.
    /// </summary>
    /// <exception cref="InvalidOperationException">It is not one of the user's videos.</exception>
    private async Task<(string Path, string Name)> StageAsync(Guid mediaId, CancellationToken ct)
    {
        var item = await media.GetAsync(user, MediaId.From(mediaId), ct);
        if (item is not { Status: MediaStatus.Ready, Kind: MediaKind.Video })
            throw new InvalidOperationException("That film is not in your Library any more. Remove it from the list and add it again.");
        var path = Path.Combine(Staging, $"{mediaId:N}{item.Extension}");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Staging);
            var scratch = Files.NewScratchFile(item.Extension);
            try
            {
                await blobs.DownloadToFileAsync(item.SourcePath, scratch, ct);
                File.Move(scratch, path, overwrite: true);
            }
            finally
            {
                File.Delete(scratch);
            }
        }
        return (path, item.Title);
    }

    /// <summary>A film as the script is told of it: where it is, how long it runs, and the owner's line on it.</summary>
    private async Task<FootageNote> NoteAsync(MovieFilmDto film, CancellationToken ct)
    {
        var (path, name) = await StageAsync(film.MediaId, ct);
        TimeSpan length;
        try
        {
            length = await engine.Builder.ProbeDurationAsync(path, ct);
        }
        catch (Exception e) when (e is FfmpegException or IOException)
        {
            throw new InvalidOperationException($"{name} could not be read.");
        }
        if (length < MinFilm || length > Conformance.MaxFootage)
            throw new InvalidOperationException($"{name} runs {length:m\\:ss}; films must be 2 seconds to 1 minute.");
        return new FootageNote(path, film.Note.Trim(), film.OwnSound || length < Conformance.MinFootage, length);
    }

    /// <summary>What a model sees happen in a film, from a few of its frames. Kept by the film, so it is asked once.</summary>
    private async Task<FootageSeen?> WatchAsync(string film, string note, Show show, CancellationToken ct)
    {
        if (!engine.ScriptLive || !engine.FfmpegAvailable) return null;
        var kept = await FootageCache.GetOrCreateAsync([$"watched-{show}", Path.GetFileName(film)], ".json", async scratch =>
        {
            var frames = await engine.Builder.FramesAsync(film, 8, ct);
            try
            {
                var seen = await Writer.WatchAsync(frames, note, show, ct) ?? throw new InvalidOperationException("Nothing here can look at pictures.");
                await File.WriteAllTextAsync(scratch, Json.Write(seen), ct);
            }
            finally
            {
                if (frames.Count > 0 && Path.GetDirectoryName(frames[0].Path) is { } folder)
                    Directory.Delete(folder, recursive: true);
            }
        });
        return Json.Read<FootageSeen>(await File.ReadAllTextAsync(kept, ct));
    }

    /// <summary>Has a model watch one film and say what it shows, for its note.</summary>
    public async Task<MovieWatchReply> SuggestNoteAsync(MovieFilmDto film, Show show, CancellationToken ct)
    {
        if (!engine.ScriptLive) return new(null, null, "A script model is needed to watch a film, and none is configured.");
        if (!engine.FfmpegAvailable) return new(null, null, "FFmpeg is needed to watch a film.");
        SpendIn(Host);
        try
        {
            var (path, _) = await StageAsync(film.MediaId, ct);
            var seen = await WatchAsync(path, film.Note, show, ct);
            return new(seen?.Note.Trim(), seen?.Timeline, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new(null, null, Plain(e));
        }
    }

    /// <summary>
    /// Watches a film, and listens to one that keeps its own sound, so the script can be written around what is in
    /// it. Neither is needed: a film that cannot be watched is placed by the owner's note alone.
    /// </summary>
    private async Task<FootageNote> UnderstandAsync(FootageNote note, Show show, CancellationToken ct)
    {
        var name = Path.GetFileName(note.Path);
        try
        {
            note = note with { Seen = await WatchAsync(note.Path, note.Note, show, ct) };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Note($"A film could not be watched, so the script follows your note on it. {Plain(e)}");
        }
        if (note.OwnSound && engine.Hear is null && await HeardOnDeviceAsync(note.Path, ct) is { Count: > 0 } onDevice)
            note = note with { Heard = onDevice };
        else if (note.OwnSound && engine.Hear is { } hear && engine.FfmpegAvailable)
            try
            {
                var kept = await FootageCache.GetOrCreateAsync(["heard", name], ".json", async scratch =>
                    await File.WriteAllTextAsync(scratch, Json.Write<IReadOnlyList<WordTiming>>(await engine.Builder.HearAsync(note.Path, hear, ct)), ct));
                note = note with { Heard = Json.Read<List<WordTiming>>(await File.ReadAllTextAsync(kept, ct)) };
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Note("What is said in a film could not be heard, so its captions are yours to type.");
            }
        return note;
    }

    /// <summary>
    /// What the browser's own speech model heard in one of the user's Library films, for a server with none: stored
    /// with the film as lines, given here a word at a time across each line. Null when nothing was heard there.
    /// </summary>
    private async Task<List<WordTiming>?> HeardOnDeviceAsync(string staged, CancellationToken ct)
    {
        if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(staged), "N", out var mediaId)) return null;
        var path = MediaAnalysisPaths.SourceSpeech(mediaId);
        if (!await blobs.ExistsAsync(path, ct)) return null;
        await using var stream = await blobs.OpenReadAsync(path, ct);
        var lines = await System.Text.Json.JsonSerializer.DeserializeAsync<List<TranscriptSegmentDto>>(stream, WebJson, ct) ?? [];
        return [.. lines.Where(l => l.EndSeconds > l.StartSeconds).SelectMany(l =>
            WordTiming.Spread(l.Text, TimeSpan.FromSeconds(l.EndSeconds - l.StartSeconds)).Select(w => w with { Start = w.Start + TimeSpan.FromSeconds(l.StartSeconds) }))];
    }

    /// <summary>Whether what is said in one of the user's Library films is known: heard by the server or by a browser.</summary>
    public Task<bool> HeardAsync(Guid mediaId, CancellationToken ct) => blobs.ExistsAsync(MediaAnalysisPaths.SourceSpeech(mediaId), ct);

    private static bool TryStructure(MovieLessonRequest request, out BeatStructure structure) =>
        Enum.TryParse(request.Structure, out structure) && Enum.IsDefined(structure)
        && Enum.TryParse<Show>(request.Show, out var show) && BeatPlan.ShowOf(structure) == show;

    /// <summary>Writes the script for a new movie, to look over clip by clip before anything is recorded.</summary>
    /// <returns>Why it could not start, or null.</returns>
    public string? WriteScript(MovieLessonRequest request)
    {
        if (request.Topic.Trim().Length == 0) return "Say what the movie is about first.";
        if (!TryStructure(request, out var structure)) return "Choose how the movie is told first.";
        if (request.Films.Length == 0) return OnlyFilms;
        if (request.Films.Length > MostFilms) return $"A movie can be made from up to {MostFilms} films. Remove {request.Films.Length - MostFilms} and try again.";
        // One clip for each film and nothing between them: the intro and outro are the only parts not filmed.
        var films = request.Films.Length;
        return Start("Writing the script", null, Host,
            (job, ct) => WriteNowAsync(job, request, request.Topic, new EpisodeLength(films, films), structure, ct), goTo: "clips");
    }

    /// <summary>
    /// Makes the whole video from the films in one go: each is watched and given a voice-over that fits its length,
    /// the voices are recorded, and the films are joined between the series' intro and outro and rendered. No
    /// pictures are drawn: the script and the voices are what it costs.
    /// </summary>
    public async Task<string?> MakeFromFilmsAsync(MovieLessonRequest request)
    {
        if (request.Films.Length == 0) return "Add a film first.";
        if (request.Films.Length > MostFilms) return $"A movie can be made from up to {MostFilms} films. Remove {request.Films.Length - MostFilms} and try again.";
        if (!engine.FfmpegAvailable) return "FFmpeg is needed to make the video, and it was not found on this server.";
        if (!TryStructure(request, out var structure)) return "Choose how the movie is told first.";
        if (OverBudget(0) is { } refusal) return refusal;
        var show = BeatPlan.ShowOf(structure);
        var topic = request.Topic.Trim() is { Length: > 0 } typed ? typed : show switch
        {
            Show.Kids => "Whatever he gets up to in these films.",
            Show.General => "Whatever happens in these films.",
            _ => "Whatever the robot does in these films.",
        };
        var preset = Preset(request.ExportPreset);
        var films = request.Films.Length;
        return await StartRenderAsync("Making the video from your films", null, Host, async (job, ct) =>
        {
            job.Phase = (0, 0.35);
            // A kids' video made this way is always the highlights of the films; the others keep the series' shape.
            var open = await WriteNowAsync(job, request, topic, new EpisodeLength(films, films), show == Show.Kids ? BeatStructure.Highlights : structure, ct);
            SpendIn(open.Folder);
            job.Phase = (0.35, 0.15);
            await NarrateEveryClipAsync(job, open, ct);
            job.Phase = (0.5, 0.5);
            await RenderMasterAsync(job, open, preset, ct);
        }, "video");
    }

    private async Task<OpenMovie> WriteNowAsync(Job job, MovieLessonRequest request, string topic, EpisodeLength length, BeatStructure structure, CancellationToken ct)
    {
        var show = BeatPlan.ShowOf(structure);
        var number = Math.Clamp(request.Number, 1, 999);
        // What the services use is kept until the movie has a folder to log it in. A script that is never finished
        // still cost what it cost: that goes with the spending made outside any movie.
        var used = new List<(string What, int Units, decimal Dollars)>();
        MovieEngine.Used.Value = (what, units, dollars) => { lock (used) used.Add((what, units, dollars)); };
        string? folder = null;
        try
        {
            job.Skeletons = [.. Enumerable.Repeat("", length.MaxClips)];
            Writer.Show = show;
            Report(job, "Fetching your films", 0);
            var notes = new List<FootageNote>();
            foreach (var film in request.Films.DistinctBy(f => f.MediaId))
                notes.Add(await NoteAsync(film, ct));
            if (TimeSpan.FromTicks(notes.Sum(n => n.Length.Ticks)) is var films && films > Durations.MaxMovie)
                throw new InvalidOperationException($"Your films run {films:m\\:ss} together; a movie can be up to 10 minutes. Leave some out and try again.");
            if (notes.Count > 0)
            {
                Report(job, "Watching your films", 0.01);
                notes = [.. await Task.WhenAll(notes.Select(n => UnderstandAsync(n, show, ct)))];
            }
            var lesson = new Lesson(topic, request.Second, structure, notes) { Earlier = ProjectStore.SeriesBefore(Episodes, number, show) };

            Report(job, "Writing the script", 0.1);
            var written = new Sink<IReadOnlyList<string>>(titles =>
            {
                job.Skeletons = [.. titles, .. Enumerable.Repeat("", Math.Max(0, length.MaxClips - titles.Count))];
                Report(job, $"Written {titles.Count} of {Math.Max(titles.Count, length.MinClips)} clips", 0.1 + 0.9 * Math.Min(1, (double)titles.Count / length.MaxClips));
            });
            var visuals = engine.VisualsFor(Host, Host, Quality.Medium, Writer);
            var episode = VisualMix.Settle(await Writer.WriteEpisodeAsync(lesson, [], length, ct, written), kind => visuals?.CanMake(kind) ?? false) with { Number = number };

            folder = ProjectStore.NewFolder(Episodes, episode.Title, number);
            Directory.CreateDirectory(folder);
            SpendIn(folder);
            episode = await BringInFootageAsync(episode, notes, folder, ct);
            var kit = SeriesKit.Load(KitFolder(show), show);
            var music = SeriesKit.PathOf(KitFolder(show), kit.Music) is { } track ? Import(track, folder, "music-" + Path.GetFileName(track)) : null;
            episode = EpisodeEditor.UseSeriesLook(episode, kit, music);
            ProjectStore.Save(episode, folder);

            var open = Opened(folder, episode);
            job.MovieId = open.Id;
            if (_lastNote is { } note) job.Message = note;
            _ = ReviewAsync(open);
            return open;
        }
        finally
        {
            List<(string What, int Units, decimal Dollars)> spent;
            lock (used) spent = [.. used];
            var into = folder is not null && File.Exists(Path.Combine(folder, ProjectStore.FileName)) ? folder : Host;
            foreach (var (what, units, dollars) in spent)
                SpendLog.Add(into, what, dollars, units);
            SpendIn(into);
            job.Skeletons = [];
        }
    }

    /// <summary>Copies each film into the movie, fits its clip's words to its length, and takes the sound of one that keeps its own.</summary>
    private async Task<Episode> BringInFootageAsync(Episode episode, IReadOnlyList<FootageNote> footage, string folder, CancellationToken ct)
    {
        foreach (var note in footage)
        {
            if (episode.Clips.FirstOrDefault(c => string.Equals(c.Visual.UserVideoPath, note.Path, StringComparison.OrdinalIgnoreCase)) is not { } clip) continue;
            if (!clip.OwnSound && note.Length < Conformance.MinFootage)
            {
                clip = BeatPlan.WithFootage(clip, note with { OwnSound = true });
                Note($"“{clip.Title}” keeps its own sound: under 5 seconds is too short to narrate over.");
            }
            var (copy, fit) = await ImportFootageAsync(clip, note.Path, note.Length, folder, ct);
            episode = EpisodeEditor.AttachVideo(EpisodeEditor.ReplaceClip(episode, clip), clip.Id, copy, fit);
            clip = episode.Clips.First(c => c.Id == clip.Id);
            if (note.Heard is { Count: > 0 } heard)
            {
                JsonFile.Save(Clip.HeardFor(copy), heard);
                if (clip.OwnSound && clip.Active.Dialogue.Trim().Length == 0)
                {
                    clip = EpisodeEditor.WithDialogue(clip, string.Join(' ', heard.Select(w => w.Text)));
                    episode = EpisodeEditor.ReplaceClip(episode, clip);
                }
            }
            if (clip.OwnSound)
                await engine.Builder.NarrateClipAsync(clip, folder, ct);
        }
        return episode;
    }

    /// <summary>
    /// Brings one film into a movie's folder for a clip. A narrated clip's words are rewritten until they take as long
    /// to say as the film runs, recording each try: that is what fitting costs.
    /// </summary>
    private async Task<(string Copy, FitResult Fit)> ImportFootageAsync(Clip clip, string path, TimeSpan length, string folder, CancellationToken ct)
    {
        var dialogue = clip.Active.Dialogue;
        var fit = new FitResult(dialogue, 1.0, length, TimeSpan.Zero, WithinTolerance: true);
        if (!clip.OwnSound)
        {
            if (dialogue.Trim().Length == 0)
                dialogue = new[] { clip.Active.VisualPrompt, clip.FootageSeen, clip.Title }.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "Look what happens here!";
            fit = await Conformance.FitAsync(dialogue, length, Writer,
                async (text, rate, c) => (await engine.Builder.NarrateClipAsync(EpisodeEditor.WithDialogue(clip, text) with { NarrationRate = rate }, folder, c)).Duration, ct);
        }
        var copy = Import(path, folder, $"{clip.Id:N}-{Path.GetFileName(path)}");
        await engine.Builder.PosterAsync(copy, ct);
        return (copy, fit);
    }

    /// <summary>Copies a file into a movie's imports under <paramref name="name"/>, unless it is there already.</summary>
    private static string Import(string path, string folder, string name)
    {
        var imports = Path.Combine(folder, "imports");
        if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(imports), StringComparison.OrdinalIgnoreCase)) return path;
        Directory.CreateDirectory(imports);
        var copy = Path.Combine(imports, name);
        File.Copy(path, copy, overwrite: true);
        return copy;
    }

    /// <summary>
    /// An editor's read of the whole script, straight after it is written: a note on each clip worth changing. A help
    /// only, so whatever goes wrong with it is let go.
    /// </summary>
    private async Task ReviewAsync(OpenMovie open)
    {
        SpendIn(open.Folder);
        try
        {
            var episode = open.Episode;
            foreach (var note in await Writer.ReviewAsync(episode, [], CancellationToken.None))
                open.Notes[episode.Clips[note.Clip - 1].Id] = note.Note;
        }
        catch (Exception)
        {
            // No notes this time.
        }
    }

    private async Task NarrateEveryClipAsync(Job job, OpenMovie open, CancellationToken ct)
    {
        var clips = open.Episode.Clips;
        var done = 0;
        await Batch.EachAsync(clips, VoicesAtOnce, async clip =>
        {
            await engine.Builder.NarrateClipAsync(clip, open.Folder, ct);
            Report(job, $"Recorded {Interlocked.Increment(ref done)} of {clips.Count} clips · {clip.Title}", (double)done / clips.Count);
        }, ct);
    }
}
