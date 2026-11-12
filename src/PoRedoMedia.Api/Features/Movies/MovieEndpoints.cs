using System.Security.Claims;
using PoRedoMedia.Movie.Library;
using PoRedoMedia.Movie.Pictures;
using PoRedoMedia.Movie.Script;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Movies;

/// <summary>
/// Create Movie over HTTP. Every route answers for the signed-in user alone: a movie's id is looked up in that user's
/// own folder, so another user's id finds nothing. Whatever changes a movie answers with the movie as it then is.
/// </summary>
public static class MovieEndpoints
{
    private const long MaxPictureBytes = 10L * 1024 * 1024;
    private const long MaxSoundBytes = 25L * 1024 * 1024; // under the server's own limit on a request

    public static IServiceCollection AddMovies(this IServiceCollection services)
    {
        services.AddHttpClient(MovieAi.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddHttpClient(MovieAi.UploadClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<MovieEngine>();
        services.AddSingleton<MovieStudios>();
        return services;
    }

    public static IEndpointRouteBuilder MapMovies(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/movies").RequireAntiforgeryValidation();

        group.MapGet("/config", (MovieEngine engine, IEnumerable<IRunStep> steps, ITranscriptionService transcription) =>
            Config(engine, steps.Any(s => s.Handles.Contains(PoRedoMedia.Shared.Enums.MediaFunction.Memeify)), transcription.IsEnabled));
        group.MapGet("/prefs", (ClaimsPrincipal user, MovieStudios studios) => studios.For(UserId.From(user)).PrefsDto());
        group.MapPut("/prefs", (MoviePrefsDto prefs, ClaimsPrincipal user, MovieStudios studios) =>
        {
            var studio = studios.For(UserId.From(user));
            studio.Preferences = new(prefs.MonthlyBudget, prefs.DraftPictures, MovieStudio.Presets.ContainsKey(prefs.ExportPreset) ? prefs.ExportPreset : "Hd30");
            return studio.PrefsDto();
        });
        group.MapGet("/", (ClaimsPrincipal user, MovieStudios studios) => studios.For(UserId.From(user)).Library());
        group.MapGet("/job", (ClaimsPrincipal user, MovieStudios studios) =>
            studios.For(UserId.From(user)).JobDto() is { } job ? Results.Ok(job) : Results.NoContent());
        group.MapDelete("/job", (ClaimsPrincipal user, MovieStudios studios) =>
        {
            studios.For(UserId.From(user)).CancelJob();
            return Results.NoContent();
        });

        MapKit(group);
        MapLesson(group);
        MapMovie(group);
        MapFiles(group);
        return app;
    }

    // ---- The brand kit ----

    private static void MapKit(RouteGroupBuilder group)
    {
        group.MapGet("/kit/{show}", (Show show, ClaimsPrincipal user, MovieStudios studios) => studios.For(UserId.From(user)).Kit(show));
        group.MapPut("/kit/{show}", (Show show, MovieKitUpdate update, ClaimsPrincipal user, MovieStudios studios) =>
            studios.For(UserId.From(user)).SaveKit(show, update) is { } problem ? Refused(problem) : Results.Ok(studios.For(UserId.From(user)).Kit(show)));

        group.MapPost("/kit/{show}/files/{role}", async (Show show, string role, string name, HttpRequest request, ClaimsPrincipal user, MovieStudios studios, CancellationToken ct) =>
        {
            if (MovieStudio.Accepts(role, name) is { } refusal) return Refused(refusal);
            var studio = studios.For(UserId.From(user));
            return await ReceiveAsync(request, name, role is "logo" or "mascot" ? MaxPictureBytes : MaxSoundBytes, file =>
                studio.SetKitFile(show, role, file) is { } problem ? Refused(problem) : Results.Ok(studio.Kit(show)), ct);
        });
        group.MapDelete("/kit/{show}/files/{role}", (Show show, string role, ClaimsPrincipal user, MovieStudios studios) =>
            studios.For(UserId.From(user)).SetKitFile(show, role, null) is { } problem ? Refused(problem) : Results.Ok(studios.For(UserId.From(user)).Kit(show)));
        group.MapGet("/kit/{show}/files/{name}", (Show show, string name, ClaimsPrincipal user, MovieStudios studios) =>
            Served(studios.For(UserId.From(user)).KitFile(show, name)));

        // The intro and outro as the kit stands: a still in a moment, the whole thing in a few seconds. All free.
        group.MapGet("/kit/{show}/still", (Show show, bool? outro, int? number, ClaimsPrincipal user, MovieStudios studios, CancellationToken ct) =>
            Drawn(studios.For(UserId.From(user)), studio => studio.BookendStillAsync(show, outro != true, number, ct)));
        group.MapPost("/kit/{show}/video", (Show show, bool? outro, int? number, ClaimsPrincipal user, MovieStudios studios, CancellationToken ct) =>
            Drawn(studios.For(UserId.From(user)), studio => studio.BookendVideoAsync(show, outro != true, number, ct)));
        group.MapPost("/kit/{show}/sound", (Show show, bool? outro, ClaimsPrincipal user, MovieStudios studios, CancellationToken ct) =>
            Drawn(studios.For(UserId.From(user)), studio => studio.BookendSoundAsync(show, outro != true, ct)));

        group.MapGet("/kit/{show}/candidates/estimate", (Show show, bool? logos, ClaimsPrincipal user, MovieStudios studios) =>
            studios.For(UserId.From(user)).CandidatesEstimate(show, logos != false));
        group.MapPost("/kit/{show}/candidates", async (Show show, bool? logos, ClaimsPrincipal user, MovieStudios studios, CancellationToken ct) =>
        {
            var (urls, problem) = await studios.For(UserId.From(user)).CandidatesAsync(show, logos != false, ct);
            return problem is null ? Results.Ok(urls) : Refused(problem);
        });
        group.MapPost("/kit/{show}/logo", (Show show, string token, ClaimsPrincipal user, MovieStudios studios) =>
        {
            var studio = studios.For(UserId.From(user));
            return studio.TempFile(token) is not { } file ? Refused("That design is no longer there. Make them again.")
                : studio.SetKitFile(show, "logo", file) is { } problem ? Refused(problem) : Results.Ok(studio.Kit(show));
        });
        group.MapPost("/kit/{show}/host", (Show show, string token, ClaimsPrincipal user, MovieStudios studios) =>
        {
            var studio = studios.For(UserId.From(user));
            if (studio.TempFile(token) is not { } file) return Refused("That design is no longer there. Make them again.");
            studio.LockHost(file);
            return Results.Ok(studio.Kit(show));
        });
        group.MapPost("/kit/{show}/host/upload", async (Show show, string name, HttpRequest request, ClaimsPrincipal user, MovieStudios studios, CancellationToken ct) =>
        {
            if (MovieStudio.Accepts("host", name) is { } refusal) return Refused(refusal);
            var studio = studios.For(UserId.From(user));
            return await ReceiveAsync(request, name, MaxPictureBytes, file =>
            {
                studio.LockHost(file);
                return Results.Ok(studio.Kit(show));
            }, ct);
        });
        group.MapPost("/kit/{show}/host-as-mascot", (Show show, ClaimsPrincipal user, MovieStudios studios) =>
            studios.For(UserId.From(user)).UseHostAsMascot(show) is { } problem ? Refused(problem) : Results.Ok(studios.For(UserId.From(user)).Kit(show)));
    }

    // ---- Starting a movie ----

    private static void MapLesson(RouteGroupBuilder group)
    {
        group.MapGet("/films/{mediaId:guid}/heard", async (Guid mediaId, ClaimsPrincipal user, MovieStudios studios, IMediaRepository media, CancellationToken ct) =>
            await media.GetAsync(UserId.From(user), MediaId.From(mediaId), ct) is null ? Results.NotFound() : Results.Ok(await studios.For(UserId.From(user)).HeardAsync(mediaId, ct)));
        group.MapPost("/films/watch", async (Show show, MovieFilmDto film, ClaimsPrincipal user, MovieStudios studios, CancellationToken ct) =>
            await studios.For(UserId.From(user)).SuggestNoteAsync(film, show, ct));
        group.MapPost("/write", (MovieLessonRequest request, ClaimsPrincipal user, MovieStudios studios) =>
            Started(studios.For(UserId.From(user)).WriteScript(request)));
        group.MapPost("/make", async (MovieLessonRequest request, ClaimsPrincipal user, MovieStudios studios) =>
            Started(await studios.For(UserId.From(user)).MakeFromFilmsAsync(request)));
    }

    // ---- One movie ----

    private static void MapMovie(RouteGroupBuilder group)
    {
        group.MapGet("/{id}", (string id, ClaimsPrincipal user, MovieStudios studios) =>
        {
            var studio = studios.For(UserId.From(user));
            try
            {
                return studio.Open(id) is { } open ? Results.Ok(new MovieReply(studio.ToDto(open))) : Results.NotFound();
            }
            catch (InvalidDataException)
            {
                return Results.Ok(new MovieReply(null, Error: "damaged"));
            }
        });
        group.MapPost("/{id}/restore", (string id, ClaimsPrincipal user, MovieStudios studios) =>
        {
            var studio = studios.For(UserId.From(user));
            try
            {
                return studio.Restore(id) is { } open ? Results.Ok(new MovieReply(studio.ToDto(open), "Restored from the copy saved just before the last change.")) : Results.NotFound();
            }
            catch (InvalidDataException)
            {
                return Refused("The saved copy is damaged too, so this movie cannot be opened.");
            }
        });
        group.MapDelete("/{id}", (string id, ClaimsPrincipal user, MovieStudios studios) =>
            !MovieStudio.IsId(id) ? Results.NotFound() : studios.For(UserId.From(user)).Delete(id) is { } problem ? Refused(problem) : Results.NoContent());
        group.MapPost("/{id}/duplicate", (string id, ClaimsPrincipal user, MovieStudios studios) =>
        {
            var studio = studios.For(UserId.From(user));
            return studio.Open(id) is null ? Results.NotFound() : Results.Ok(new MovieReply(null, "Copied.", Url: studio.Duplicate(id)));
        });

        group.MapPost("/{id}/edits", (string id, MovieEdit edit, ClaimsPrincipal user, MovieStudios studios) =>
            With(studios, user, id, (studio, open) => (studio.Apply(open, edit), null)));
        group.MapPost("/{id}/undo", (string id, ClaimsPrincipal user, MovieStudios studios) => With(studios, user, id, (studio, open) => (studio.Undo(open), null)));
        group.MapPost("/{id}/redo", (string id, ClaimsPrincipal user, MovieStudios studios) => With(studios, user, id, (studio, open) => (studio.Redo(open), null)));

        // A clip's words, voice and picture. These ask a service, so each takes a moment and answers when it is done.
        group.MapPost("/{id}/clips/{clip:guid}/tier", (string id, Guid clip, string tier, ClaimsPrincipal user, MovieStudios studios, CancellationToken ct) =>
            WithAsync(studios, user, id, async (studio, open) => (await studio.PickTierAsync(open, clip, tier, ct), null)));
        group.MapPost("/{id}/clips/{clip:guid}/dialogue", (string id, Guid clip, MovieEdit edit, ClaimsPrincipal user, MovieStudios studios, CancellationToken ct) =>
            WithAsync(studios, user, id, async (studio, open) => (await studio.SetDialogueAsync(open, clip, edit.Text ?? "", ct), null)));
        group.MapPost("/{id}/clips/{clip:guid}/answer", (string id, Guid clip, ClaimsPrincipal user, MovieStudios studios, CancellationToken ct) =>
            WithAsync(studios, user, id, (studio, open) => studio.AnswerNoteAsync(open, clip, ct)));
        group.MapPost("/{id}/clips/{clip:guid}/picture", (string id, Guid clip, bool? another, ClaimsPrincipal user, MovieStudios studios, CancellationToken ct) =>
            WithAsync(studios, user, id, async (studio, open) => (await studio.GeneratePictureAsync(open, clip, another == true, ct), null)));
        group.MapPost("/{id}/clips/{clip:guid}/film", (string id, Guid clip, Guid mediaId, ClaimsPrincipal user, MovieStudios studios) =>
            With(studios, user, id, (studio, open) => (studio.AttachFilm(open, clip, mediaId), null)));
        group.MapPost("/{id}/clips/{clip:guid}/own-sound", (string id, Guid clip, bool on, ClaimsPrincipal user, MovieStudios studios, CancellationToken ct) =>
            WithAsync(studios, user, id, async (studio, open) => (await studio.SetOwnSoundAsync(open, clip, on, ct), null)));
        group.MapPost("/{id}/clips/{clip:guid}/play", (string id, Guid clip, ClaimsPrincipal user, MovieStudios studios, CancellationToken ct) =>
            PlayAsync(studios, user, id, (studio, open) => studio.ClipPreviewAsync(open, clip, ct)));

        // The long jobs. Each starts and answers at once; its progress is read from /job.
        group.MapPost("/{id}/voices", (string id, ClaimsPrincipal user, MovieStudios studios) => With(studios, user, id, (studio, open) => (studio.NarrateAll(open), null)));
        group.MapGet("/{id}/pictures/estimate", (string id, bool? finals, ClaimsPrincipal user, MovieStudios studios) =>
            studios.For(UserId.From(user)) is var studio && studio.Open(id) is { } open ? Results.Ok(studio.PictureEstimate(open, finals == true)) : Results.NotFound());
        group.MapPost("/{id}/pictures", (string id, bool? finals, ClaimsPrincipal user, MovieStudios studios) =>
            With(studios, user, id, (studio, open) => (studio.GenerateAll(open, finals == true), null)));
        group.MapPost("/{id}/preview", (string id, ClaimsPrincipal user, MovieStudios studios) => With(studios, user, id, (studio, open) => (studio.BuildPreview(open), null)));
        group.MapGet("/{id}/memes/estimate", (string id, ClaimsPrincipal user, MovieStudios studios) =>
            studios.For(UserId.From(user)) is var studio && studio.Open(id) is { } open ? Results.Ok(studio.MemeEstimate(open)) : Results.NotFound());
        group.MapPost("/{id}/render", (string id, string? preset, ClaimsPrincipal user, MovieStudios studios) =>
            WithAsync(studios, user, id, async (studio, open) => (await studio.RenderAsync(open, preset), null)));
        group.MapPost("/{id}/thumbnails", (string id, ClaimsPrincipal user, MovieStudios studios) => With(studios, user, id, (studio, open) => (studio.MakeThumbnails(open), null)));
        group.MapPost("/{id}/thumbnail", (string id, int index, ClaimsPrincipal user, MovieStudios studios) =>
            With(studios, user, id, (studio, open) => (studio.PickThumbnail(open, index), "Thumbnail saved. It goes with the video when it is uploaded.")));

        group.MapPost("/{id}/teasers/suggest", (string id, ClaimsPrincipal user, MovieStudios studios) => With(studios, user, id, (studio, open) => (studio.SuggestTeasers(open), null)));
        group.MapPut("/{id}/teasers", (string id, MovieTeasersUpdate update, ClaimsPrincipal user, MovieStudios studios) => With(studios, user, id, (_, open) =>
        {
            // A hook is eight words at most; the engine leaves off any more, so they are left off here too.
            open.Teasers = [.. update.Teasers.Take(PoRedoMedia.Movie.Render.Teasers.MaxTeasers).Select(t => t with { Hook = t.Hook.Length > 80 ? t.Hook[..80] : t.Hook })];
            return (null, null);
        }));
        group.MapPost("/{id}/teasers/{index:int}/play", (string id, int index, ClaimsPrincipal user, MovieStudios studios, CancellationToken ct) =>
            PlayAsync(studios, user, id, (studio, open) => studio.TeaserDraftAsync(open, index, ct)));
        group.MapPost("/{id}/teasers/render", (string id, ClaimsPrincipal user, MovieStudios studios) => With(studios, user, id, (studio, open) => (studio.RenderTeasers(open), null)));

        group.MapPost("/{id}/music", async (string id, string name, HttpRequest request, ClaimsPrincipal user, MovieStudios studios, CancellationToken ct) =>
        {
            if (MovieStudio.Accepts("music", name) is { } refusal) return Refused(refusal);
            var studio = studios.For(UserId.From(user));
            if (studio.Open(id) is not { } open) return Results.NotFound();
            return await ReceiveAsync(request, name, MaxSoundBytes, file => studio.SetMusic(open, file, name) is { } problem
                ? Refused(problem)
                : Results.Ok(new MovieReply(studio.ToDto(open), "Music added. It plays over the outro at the end, from the next preview or render.")), ct);
        });
        group.MapGet("/{id}/unused", (string id, ClaimsPrincipal user, MovieStudios studios) =>
        {
            var studio = studios.For(UserId.From(user));
            if (studio.Open(id) is not { } open) return Results.NotFound();
            var (files, megabytes) = studio.UnusedMedia(open);
            return Results.Ok(new MovieEstimateDto(
                FormattableString.Invariant($"{files} files ({megabytes:0.0} MB) are no longer used by any clip: recordings of earlier wording and replaced pictures. They are deleted for good, and a removed picture would be charged again if you went back to it. Undo history is cleared."),
                null, files == 0, null));
        });
        group.MapPost("/{id}/clean", (string id, ClaimsPrincipal user, MovieStudios studios) =>
            With(studios, user, id, (studio, open) => studio.IsWorking ? (MovieStudio.Busy, null) : (null, studio.CleanMedia(open))));

        group.MapPost("/{id}/library", (string id, ClaimsPrincipal user, MovieStudios studios, CancellationToken ct) =>
            WithAsync(studios, user, id, async (studio, open) =>
                await studio.SaveToLibraryAsync(open, ct) is (_, { } problem) ? (problem, null) : (null, "The video is in your Library.")));
        group.MapPost("/{id}/youtube", (string id, MovieUploadRequest upload, HttpRequest request, ClaimsPrincipal user, MovieStudios studios) =>
        {
            var studio = studios.For(UserId.From(user));
            if (studio.Open(id) is not { } open) return Results.NotFound();
            var (url, problem) = studio.BeginUpload(open, upload, $"{request.Scheme}://{request.Host}/api/movies/youtube/callback");
            return problem is null ? Results.Ok(new MovieReply(null, Url: url)) : Refused(problem);
        });
        // Google sends the user's own browser back here, signed in as them, with the code for the upload they asked for.
        group.MapGet("/youtube/callback", (string? state, string? code, ClaimsPrincipal user, MovieStudios studios) =>
            Results.Text(studios.For(UserId.From(user)).CompleteUpload(state, code), "text/plain; charset=utf-8"));
    }

    // ---- Files ----

    private static void MapFiles(RouteGroupBuilder group)
    {
        group.MapGet("/{id}/files/{**path}", (string id, string path, ClaimsPrincipal user, MovieStudios studios) =>
            Served(studios.For(UserId.From(user)).FileOf(id, path)));
        group.MapGet("/temp/{token}", (string token, ClaimsPrincipal user, MovieStudios studios) =>
            Served(studios.For(UserId.From(user)).TempFile(token)));
    }

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".wav"] = "audio/wav",
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".flac"] = "audio/flac",
        [".ogg"] = "audio/ogg",
        [".srt"] = "text/plain; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
    };

    /// <summary>A media file, with ranges so a player can seek. Anything that is not media the browser plays or shows is not served.</summary>
    private static IResult Served(string? path)
    {
        if (path is null || !ContentTypes.TryGetValue(Path.GetExtension(path), out var type) || !File.Exists(path))
            return Results.NotFound();

        // FileShare.Delete lets the user delete a movie cleanly even while a range request finishes closing on Windows.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 64 * 1024, useAsync: true);
        return Results.Stream(stream, type, lastModified: File.GetLastWriteTimeUtc(path), enableRangeProcessing: true);
    }

    // ---- Shapes the routes share ----

    private static IResult Refused(string reason) => Results.Problem(detail: reason, statusCode: StatusCodes.Status400BadRequest);

    private static IResult Started(string? problem) => problem is null ? Results.Ok(new MovieReply(null)) : Refused(problem);

    /// <summary>Does something to the user's movie and answers with the movie as it then is, and what to say about it.</summary>
    private static IResult With(MovieStudios studios, ClaimsPrincipal user, string id, Func<MovieStudio, OpenMovie, (string? Error, string? Status)> act)
    {
        var studio = studios.For(UserId.From(user));
        try
        {
            if (studio.Open(id) is not { } open) return Results.NotFound();
            var (error, status) = act(studio, open);
            return Results.Ok(new MovieReply(studio.ToDto(open), error is null ? status : null, error));
        }
        catch (InvalidDataException e)
        {
            return Refused(e.Message);
        }
    }

    private static async Task<IResult> WithAsync(MovieStudios studios, ClaimsPrincipal user, string id, Func<MovieStudio, OpenMovie, Task<(string? Error, string? Status)>> act)
    {
        var studio = studios.For(UserId.From(user));
        try
        {
            if (studio.Open(id) is not { } open) return Results.NotFound();
            var (error, status) = await act(studio, open);
            return Results.Ok(new MovieReply(studio.ToDto(open), error is null ? status : null, error));
        }
        catch (InvalidDataException e)
        {
            return Refused(e.Message);
        }
    }

    private static async Task<IResult> PlayAsync(MovieStudios studios, ClaimsPrincipal user, string id, Func<MovieStudio, OpenMovie, Task<(string? Url, string? Error)>> make)
    {
        var studio = studios.For(UserId.From(user));
        if (studio.Open(id) is not { } open) return Results.NotFound();
        var (url, error) = await make(studio, open);
        return Results.Ok(new MovieReply(studio.ToDto(open), Error: error, Url: url));
    }

    private static async Task<IResult> Drawn(MovieStudio studio, Func<MovieStudio, Task<string>> draw)
    {
        if (!studio.Engine.FfmpegAvailable) return Refused("FFmpeg is needed to draw the intro, and it was not found on this server.");
        try
        {
            return Results.Ok(new MovieReply(null, Url: await draw(studio)));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Refused(MovieStudio.Plain(e));
        }
    }

    /// <summary>
    /// Takes a file sent as the request's body into a working file, no larger than <paramref name="most"/>, hands it
    /// to <paramref name="use"/> and removes it. The file's name is only ever used for its type.
    /// </summary>
    private static async Task<IResult> ReceiveAsync(HttpRequest request, string name, long most, Func<string, IResult> use, CancellationToken ct)
    {
        if (request.ContentLength > most) return Refused($"That file is too large: {most / (1024 * 1024)} MB at most.");
        var scratch = Files.NewScratchFile(Path.GetExtension(name).ToLowerInvariant());
        try
        {
            await using (var file = File.Create(scratch))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
                {
                    if ((total += read) > most) return Refused($"That file is too large: {most / (1024 * 1024)} MB at most.");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (total == 0) return Refused("The file is empty.");
            }
            return use(scratch);
        }
        finally
        {
            File.Delete(scratch);
        }
    }

    // ---- What this server offers ----

    private static MovieConfigDto Config(MovieEngine engine, bool memes, bool serverSpeech)
    {
        static MovieOption[] Structures(Show show) =>
            [.. BeatPlan.Structures(show).Select(s => new MovieOption(s.ToString(), BeatPlan.Label(s), string.Join(" → ", BeatPlan.Beats(s).Select(b => b.ForFootage ? b.Name + " 🎥" : b.Name))))];

        var visuals = engine.VisualsFor(engine.Root, engine.Root, Quality.Medium, new MockScriptWriter());
        return new MovieConfigDto(
            engine.FfmpegAvailable, engine.ScriptLive, engine.VoiceLive, engine.PicturesAvailable, engine.PicturesAvailable && ImageModels.HasQuality(engine.ImageModel), engine.YouTubeAvailable,
            [
                new("General", "General", "A movie about anything: a trip, a project, a party, a pet. A plain, friendly voice-over tells it.", Structures(Show.General),
                    "What the movie is about", "Our weekend building the treehouse", "Moments not to miss (optional)", "The ladder slipping, and the first climb up", false),
                new("Robot", "Robot lesson", "An episode of the R1 series: teaching the robot one skill, told by a cartoon version of the robot.", Structures(Show.Robot),
                    "What I taught it", "Walking up three stairs without the handrail", "What went wrong (optional)", "It leaned back and sat down on the second step", false),
                new("Kids", "Kids' video", "A video for young children of your son playing, told by a cheerful narrator and uploaded as made for kids. No picture is ever drawn of him.", Structures(Show.Kids),
                    "What he plays with", "Opening a surprise egg with a dinosaur inside", "Funny moments (optional)", "He tried to feed the dinosaur a crayon", true),
            ],
            [new("Comic", "Comic book"), new("Photoreal", "Photograph"), new("FlatVector", "Flat vector"), new("Cinematic", "Cinematic")],
            [.. Visuals.Kinds.Where(k => visuals?.CanMake(k.Kind) ?? k.Kind == VisualKind.TitleCard).Select(k => new MovieOption(k.Kind.ToString(), k.Label))],
            [new("KaraokeHighlight", "Karaoke highlight"), new("TwoLineBlock", "Two-line block"), new("CleanSubtitle", "Clean subtitle"), new("ComicBanner", "Comic banner")],
            [new("Side", "Side"), new("Centre", "Centre"), new("Lower", "Lower band")],
            [new("Hd30", "1080p, 30 fps"), new("Hd60", "1080p, 60 fps"), new("Uhd30", "4K, 30 fps"), new("Uhd60", "4K, 60 fps")],
            [new("Workshop", "#22CC88", "Side"), new("Blueprint", "#2F80ED", "Centre"), new("Safety amber", "#FFB020", "Lower"), new("Night shift", "#8B5CF6", "Centre")],
            engine.Notice, MovieStudio.MostFilms, IScriptWriter.MaxTopicLength,
            memes, engine.MemePrice, 60, (int)Durations.MaxMovie.TotalSeconds, serverSpeech);
    }
}
