using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using PoRedoMedia.Movie.Library;
using PoRedoMedia.Movie.Pictures;
using PoRedoMedia.Movie.Render;
using PoRedoMedia.Movie.Script;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Movies;

/// <summary>One user's studios, made the first time each is asked for and kept while the server runs.</summary>
public sealed class MovieStudios(MovieEngine engine, IMediaRepository media, BlobStorageService blobs, Thumbnails thumbnails, IEnumerable<IRunStep> steps, IRenderQuota quota)
{
    private readonly ConcurrentDictionary<string, MovieStudio> _studios = new();

    /// <summary>The step that runs Meme-ify (and the roast) for the Create page, which every movie clip goes through too.</summary>
    private readonly IRunStep? _memes = steps.FirstOrDefault(s => s.Handles.Contains(PoRedoMedia.Shared.Enums.MediaFunction.Memeify));

    public MovieStudio For(UserId user) => _studios.GetOrAdd(user.Value, _ => new MovieStudio(engine, user, media, blobs, thumbnails, _memes, quota));
}

/// <summary>A movie that is open: the episode as it is now, how to step back through its changes, and what has been made of it this session.</summary>
public sealed class OpenMovie(string id, string folder, Episode episode)
{
    public string Id { get; } = id;

    public string Folder { get; } = folder;

    public Episode Episode { get; set; } = episode;

    public EditHistory<Episode> History { get; } = new();

    /// <summary>The editor's notes on clips' words, from the read of the script. Kept for the session only.</summary>
    public ConcurrentDictionary<Guid, string> Notes { get; } = new();

    public Preview? Preview { get; set; }

    public List<MovieTeaserDto> Teasers { get; set; } = [];

    public IReadOnlyList<string> ThumbnailChoices { get; set; } = [];

    public string? VideoLink { get; set; }

    public Guid? LibraryId { get; set; }

    /// <summary>One picture at a time is asked for by hand, so the same request is never paid for twice.</summary>
    public SemaphoreSlim PictureGate { get; } = new(1, 1);
}

/// <summary>
/// Everything one user does in Create Movie: their saved movies, the brand kit of each series, and the one long job
/// they can have under way. It stands where PoAutoRobo's window stood: the engine does the work, and this decides
/// what is asked of it, in what order, and what is said about it afterwards.
/// </summary>
/// <remarks>
/// ponytail: open movies, undo history and the job live in this server's memory, as they lived in the desktop app's.
/// A restart forgets the undo steps and stops a job; everything made is on disk and is picked up again.
/// </remarks>
public sealed partial class MovieStudio(MovieEngine engine, UserId user, IMediaRepository media, BlobStorageService blobs, Thumbnails thumbnails, IRunStep? memes, IRenderQuota quota)
{
    private readonly ConcurrentDictionary<string, OpenMovie> _open = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, string> _temp = new();
    private readonly ConcurrentQueue<string> _tempOrder = new();
    private IScriptWriter? _writer;
    private string? _lastNote;

    private string Home => engine.UserFolder(user);

    public string Episodes => Path.Combine(Home, "episodes");

    /// <summary>The locked host design, its candidates, and the spending made with no movie open.</summary>
    private string Host => Path.Combine(Home, "host");

    private string Staging => Path.Combine(Home, "staging");

    private MediaCache FootageCache => new(Path.Combine(Home, "footage"));

    public string KitFolder(Show show) => Path.Combine(Home, show switch { Show.Kids => "series-kids", Show.General => "series-general", _ => "series" });

    private IScriptWriter Writer => _writer ??= engine.NewWriter(Note);

    public MovieEngine Engine => engine;

    // ---- Preferences ----

    public sealed record Prefs(decimal MonthlyBudget = 0, bool DraftPictures = false, string ExportPreset = "Hd30");

    private string PrefsFile => Path.Combine(Home, "prefs.json");

    public Prefs Preferences
    {
        get => MovieJson.Load(PrefsFile, new Prefs());
        set => MovieJson.Save(PrefsFile, value with { MonthlyBudget = Math.Max(0, value.MonthlyBudget) });
    }

    public MoviePrefsDto PrefsDto() => Preferences is var prefs
        ? new(prefs.MonthlyBudget, prefs.DraftPictures, prefs.ExportPreset, SpendLog.MonthTotal(Episodes, DateTimeOffset.Now, Host)) : null!;

    private Quality Quality => Preferences.DraftPictures ? Quality.Low : Quality.Medium;

    private bool IsDraft(Quality quality) => quality == Quality.Low && ImageModels.HasQuality(engine.ImageModel);

    public static readonly IReadOnlyDictionary<string, ExportPreset> Presets = new Dictionary<string, ExportPreset>
    {
        ["Hd30"] = ExportPreset.Hd30, ["Hd60"] = ExportPreset.Hd60, ["Uhd30"] = ExportPreset.Uhd30, ["Uhd60"] = ExportPreset.Uhd60,
    };

    private ExportPreset Preset(string? name = null) => Presets.GetValueOrDefault(name ?? Preferences.ExportPreset, ExportPreset.Hd30);

    // ---- Spending ----

    /// <summary>Why spending <paramref name="more"/> cannot go ahead (it would pass the month's budget), or null.</summary>
    public string? OverBudget(decimal more)
    {
        var budget = Preferences.MonthlyBudget;
        if (budget <= 0) return null;
        var spent = SpendLog.MonthTotal(Episodes, DateTimeOffset.Now, Host);
        return spent + more > budget
            ? $"That would pass this month's budget of {SpendLog.Money(budget)}: {SpendLog.Money(spent)} is spent already. Raise the budget in Settings to go on."
            : null;
    }

    /// <summary>Sends what the services use on this request or job to <paramref name="folder"/>'s spending.</summary>
    private static void SpendIn(string folder) => MovieEngine.Used.Value = (what, units, dollars) => SpendLog.Add(folder, what, dollars, units);

    // ---- Messages ----

    /// <summary>Something out of the ordinary to tell the user, said by the engine part-way through a job.</summary>
    private void Note(string message)
    {
        _lastNote = message;
        if (_job is { State: JobState.Running } job) job.Detail = message;
    }

    /// <summary>What went wrong, in words fit to show.</summary>
    public static string Plain(Exception e) => e switch
    {
        HttpRequestException => "The service could not be reached. Check the connection and try again.",
        System.ComponentModel.Win32Exception => "A program the app needs could not be started. FFmpeg and ffprobe must be installed on the server.",
        InvalidOperationException or InvalidDataException or ArgumentException or IOException or FfmpegException => e.Message,
        _ => e.Message.Split('\n')[0].Trim(),
    };

    // ---- The one long job ----

    public enum JobState { Running, Done, Stopped, Failed }

    public sealed class Job(int id, string title, string? movieId)
    {
        public int Id { get; } = id;
        public string Title { get; } = title;
        public string? MovieId { get; set; } = movieId;
        public string Detail { get; set; } = "Starting…";
        public double Fraction { get; set; }
        public (double From, double Span) Phase { get; set; } = (0, 1);
        public JobState State { get; set; } = JobState.Running;
        public string? Message { get; set; }
        public string? Error { get; set; }
        public string? GoTo { get; set; }
        public string[] Skeletons { get; set; } = [];
        public double[]? ClipProgress { get; set; }
        public long Started { get; } = Stopwatch.GetTimestamp();
        public TimeSpan? Took { get; set; }

        /// <summary>The finished video was made: the run it used is spent, whatever happens after.</summary>
        public bool Made { get; set; }
        public CancellationTokenSource Cancel { get; } = new();
    }

    private Job? _job;
    private int _jobs;

    public bool IsWorking => _job is { State: JobState.Running };

    public const string Busy = "Wait for the job in progress to finish, or cancel it.";

    /// <summary>
    /// Starts the user's one long job and returns at once; its progress is read with <see cref="JobDto"/>. A job that
    /// is stopped says so; one that fails says why, in words fit to show.
    /// </summary>
    /// <returns>Null when it started, or why it could not.</returns>
    public string? Start(string title, string? movieId, string? spendFolder, Func<Job, CancellationToken, Task> work, string? doneMessage = null, string? goTo = null)
    {
        Job job;
        lock (_gate)
        {
            if (IsWorking) return Busy;
            _job = job = new Job(++_jobs, title, movieId);
        }
        _lastNote = null;
        _ = Task.Run(async () =>
        {
            SpendIn(spendFolder ?? Host);
            try
            {
                await work(job, job.Cancel.Token);
                job.Message ??= doneMessage;
                job.GoTo ??= goTo;
                job.State = JobState.Done;
            }
            catch (OperationCanceledException) when (job.Cancel.IsCancellationRequested)
            {
                job.Message = job.Message is { } said ? $"Stopped. {said}" : "Stopped.";
                job.State = JobState.Stopped;
            }
            catch (Exception e)
            {
                job.Error = Plain(e);
                job.State = JobState.Failed;
            }
            finally
            {
                job.Took = Stopwatch.GetElapsedTime(job.Started);
                job.ClipProgress = null;
                job.Cancel.Dispose();
            }
        });
        return null;
    }

    /// <summary>
    /// Starts a job that ends in a finished movie: the script, the voices, Meme-ify and the render are one run of the
    /// user's daily allowance, like any run on the Create page. A job that ends without a video gives it back.
    /// </summary>
    /// <returns>Null when it started, or why it could not.</returns>
    private async Task<string?> StartRenderAsync(string title, string? movieId, string spendFolder, Func<Job, CancellationToken, Task> work, string goTo)
    {
        if (IsWorking) return Busy;
        var (allowed, status) = await quota.TryConsumeAsync(user);
        if (!allowed)
            return $"Making a movie uses a run, and all {status.Limit} for today are used. More are available at {status.ResetsAt:HH:mm} UTC.";
        var problem = Start(title, movieId, spendFolder, async (job, ct) =>
        {
            try
            {
                await work(job, ct);
            }
            finally
            {
                if (!job.Made)
                    await quota.RefundAsync(user, CancellationToken.None);
            }
        }, goTo: goTo);
        if (problem is not null)
            await quota.RefundAsync(user);
        return problem;
    }

    public void CancelJob()
    {
        if (_job is not { State: JobState.Running } job) return;
        job.Detail = "Stopping…";
        try
        {
            job.Cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It ended just now.
        }
    }

    private static void Report(Job job, string detail, double fraction)
    {
        job.Detail = detail;
        job.Fraction = Math.Max(job.Fraction, job.Phase.From + job.Phase.Span * Math.Clamp(fraction, 0, 1));
    }

    private static Sink<RenderProgress> Rendering(Job job) => new(p =>
    {
        Report(job, p.Activity, p.Fraction);
        job.ClipProgress = p.Clips is null ? null : [.. p.Clips];
    });

    /// <summary>Reports on the calling thread, so values arrive in the order they were sent.</summary>
    private sealed class Sink<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public MovieJobDto? JobDto()
    {
        if (_job is not { } job) return null;
        var elapsed = job.Took ?? Stopwatch.GetElapsedTime(job.Started);
        var time = $"{elapsed:m\\:ss} so far";
        if (job.State == JobState.Running && job.Fraction > 0.03)
        {
            var left = elapsed * ((1 - job.Fraction) / job.Fraction);
            time += left.TotalMinutes >= 1 ? $" · about {Math.Round(left.TotalMinutes):0} min left" : " · under a minute left";
        }
        return new MovieJobDto(job.Id, job.Title, job.Detail, job.Fraction, time, job.State.ToString().ToLowerInvariant(), job.MovieId,
            job.Message, job.Error, job.Skeletons, job.ClipProgress, job.GoTo);
    }

    // ---- Opening and saving ----

    /// <summary>A movie's id is its folder's name, which the engine makes from letters, digits and hyphens alone.</summary>
    public static bool IsId(string? id) => id is { Length: > 0 and <= 80 } && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>The open movie, read from disk the first time it is asked for; null when there is none by that id.</summary>
    /// <exception cref="InvalidDataException">Its file is damaged; <see cref="Restore"/> may bring it back.</exception>
    public OpenMovie? Open(string id)
    {
        if (!IsId(id)) return null;
        if (_open.TryGetValue(id, out var open)) return open;
        var folder = Path.Combine(Episodes, id);
        if (!File.Exists(Path.Combine(folder, ProjectStore.FileName))) return null;
        open = _open.GetOrAdd(id, new OpenMovie(id, folder, ProjectStore.Load(folder)) { LibraryId = LibraryIdOf(folder) });
        Writer.Show = BeatPlan.ShowOf(open.Episode.Structure);
        return open;
    }

    /// <summary>Opens a damaged movie from the copy saved just before its last change.</summary>
    public OpenMovie? Restore(string id)
    {
        if (!IsId(id)) return null;
        var folder = Path.Combine(Episodes, id);
        if (!File.Exists(Path.Combine(folder, ProjectStore.FileName + ".bak"))) return null;
        _open[id] = new OpenMovie(id, folder, ProjectStore.RestoreBackup(folder));
        return _open[id];
    }

    private OpenMovie Opened(string folder, Episode episode)
    {
        var id = Path.GetFileName(folder);
        var open = new OpenMovie(id, folder, episode);
        _open[id] = open;
        Writer.Show = BeatPlan.ShowOf(episode.Structure);
        return open;
    }

    /// <summary>
    /// Every change to a movie comes through here: it is saved, then remembered so it can be undone. A run of caption
    /// changes (a slider being dragged) is undone as one.
    /// </summary>
    /// <returns>Why it could not be saved, or null.</returns>
    public string? Edit(OpenMovie open, Func<Episode, Episode> change)
    {
        lock (_gate)
        {
            try
            {
                var before = open.Episode;
                var updated = change(before);
                if (ReferenceEquals(updated, before)) return null;
                var captionsOnly = (updated with { Captions = before.Captions }) == before;
                ProjectStore.Save(updated, open.Folder);
                open.Episode = updated;
                open.History.Record(before, merges: captionsOnly);
                return null;
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return e.Message;
            }
        }
    }

    public string? Undo(OpenMovie open) => Travel(open, undo: true);

    public string? Redo(OpenMovie open) => Travel(open, undo: false);

    private string? Travel(OpenMovie open, bool undo)
    {
        lock (_gate)
        {
            var now = open.Episode;
            if (!(undo ? open.History.TryUndo(now, out var target) : open.History.TryRedo(now, out target))) return null;
            try
            {
                ProjectStore.Save(target, open.Folder);
                open.Episode = target;
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not saved, so not done: the step is put back where it was.
                _ = undo ? open.History.TryRedo(target, out _) : open.History.TryUndo(target, out _);
                return e.Message;
            }
        }
    }

    // ---- Files the browser is shown ----

    /// <summary>A link to a file inside a movie's folder. It carries the file's time, so a file made again is fetched again.</summary>
    private static string? Link(OpenMovie open, string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        var relative = Path.GetRelativePath(open.Folder, path).Replace('\\', '/');
        return relative.StartsWith("..", StringComparison.Ordinal) ? null
            : $"/api/movies/{open.Id}/files/{string.Join('/', relative.Split('/').Select(Uri.EscapeDataString))}?v={File.GetLastWriteTimeUtc(path).Ticks:x}";
    }

    /// <summary>A file of <paramref name="id"/>'s the browser asked for, when it is really inside that movie's folder.</summary>
    public string? FileOf(string id, string relative)
    {
        if (!IsId(id)) return null;
        var root = Path.GetFullPath(Path.Combine(Episodes, id)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    /// <summary>
    /// A link to a working file outside any movie: a still of the intro, a clip's quick preview, a thumbnail to choose.
    /// The link is a name nobody could guess and is good while the server runs; only the newest few hundred are kept.
    /// </summary>
    public string Temp(string path)
    {
        var token = _temp.FirstOrDefault(p => p.Value == path).Key;
        if (token is null)
        {
            token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant() + Path.GetExtension(path).ToLowerInvariant();
            _temp[token] = path;
            _tempOrder.Enqueue(token);
            while (_tempOrder.Count > 300 && _tempOrder.TryDequeue(out var old)) _temp.TryRemove(old, out _);
        }
        return $"/api/movies/temp/{token}?v={(File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0):x}";
    }

    public string? TempFile(string token) => _temp.TryGetValue(token, out var path) && File.Exists(path) ? path : null;

    // ---- The library ----

    public IReadOnlyList<MovieSummaryDto> Library() =>
        [.. ProjectStore.Summaries(Episodes, engine.Builder.MeasuredDuration).Select(s => new MovieSummaryDto(
            Path.GetFileName(s.Folder), s.Title, s.Show.ToString(), s.Details,
            s.Thumbnail is { } picture ? $"/api/movies/{Path.GetFileName(s.Folder)}/files/{string.Join('/', Path.GetRelativePath(s.Folder, picture).Replace('\\', '/').Split('/').Select(Uri.EscapeDataString))}" : null,
            s.Saved, s.Number, s.Clips == 0))];

    /// <returns>The copy's id.</returns>
    public string Duplicate(string id) => Path.GetFileName(ProjectStore.Duplicate(Path.Combine(Episodes, id), Episodes));

    /// <returns>Why it could not be deleted, or null.</returns>
    public string? Delete(string id)
    {
        if (_job is { State: JobState.Running, MovieId: var busy } && busy == id) return Busy;
        _open.TryRemove(id, out _);
        try
        {
            Directory.Delete(Path.Combine(Episodes, id), recursive: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"The movie could not be deleted: {e.Message}";
        }
    }

    /// <summary>What unused recordings, pictures and films would be removed, and their size; nothing is removed yet.</summary>
    public (int Files, double Megabytes) UnusedMedia(OpenMovie open)
    {
        var unused = engine.Builder.UnusedMedia(open.Episode, open.Folder);
        return (unused.Count, unused.Sum(f => new FileInfo(f).Length) / 1_048_576.0);
    }

    /// <summary>Removes them. Undo could point at a file that has gone, so the undo steps are forgotten.</summary>
    public string CleanMedia(OpenMovie open)
    {
        var unused = engine.Builder.UnusedMedia(open.Episode, open.Folder);
        if (unused.Count == 0) return "Nothing to remove: every saved file is in use.";
        var megabytes = unused.Sum(f => new FileInfo(f).Length) / 1_048_576.0;
        lock (_gate) open.History.Clear();
        foreach (var file in unused) File.Delete(file);
        return string.Create(CultureInfo.InvariantCulture, $"Removed {unused.Count} unused files ({megabytes:0.0} MB).");
    }
}

/// <summary>How the studio's own small files are written: as indented JSON, enums by name.</summary>
internal static class MovieJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>What the file holds, or <paramref name="fallback"/> when it is missing or unreadable.</summary>
    public static T Load<T>(string path, T fallback)
    {
        try
        {
            return File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback : fallback;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            return fallback;
        }
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(value, Options));
    }
}
