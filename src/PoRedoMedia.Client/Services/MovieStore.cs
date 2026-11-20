using Microsoft.JSInterop;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Client.Services;

/// <summary>A film on the Films step: one of the Library's videos, with the note that says where it belongs.</summary>
public sealed class MovieFilm(MediaDto media)
{
    public MediaDto Media { get; } = media;

    public string Note { get; set; } = "";

    /// <summary>It plays with its own sound; otherwise it is narrated over. A film under five seconds is too short to narrate.</summary>
    public bool OwnSound { get; set; } = media.DurationSeconds is < 5;

    public bool Watching { get; set; }

    /// <summary>What the browser's own speech model is doing with it, for a server with none; null when nothing is.</summary>
    public string? Listening { get; set; }

    /// <summary>What is said in it is known: heard by the server, or here.</summary>
    public bool Heard { get; set; }

    public TimeSpan Length => TimeSpan.FromSeconds(Media.DurationSeconds ?? 0);
}

/// <summary>
/// What Create Movie holds while it is open: the server's choices, the open movie, the form for the next one, the
/// long job under way and the last thing said. The pages draw from it and ask it to act; it raises
/// <see cref="Changed"/> whenever what they show has moved on.
/// </summary>
public sealed class MovieStore(MovieApi api, IJSRuntime js, AppSettings settings) : IDisposable
{
    private const string LastKey = "poredomedia:movie:last";
    private CancellationTokenSource? _polling;
    private bool _started;

    public event Action? Changed;

    public MovieConfigDto? Config { get; private set; }

    public MovieDto? Movie { get; private set; }

    public MovieJobDto? Job { get; private set; }

    public List<MovieSummaryDto> Library { get; private set; } = [];

    public MoviePrefsDto Prefs { get; private set; } = new(0, false, "Hd30", 0);

    public string? Status { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Something worth knowing that is not a failure (a film could not be heard); stays until it is closed.</summary>
    public string? Notice { get; private set; }

    /// <summary>A small video to play over the page: a teaser, watched on its own.</summary>
    public (string Url, bool Upright)? Playing { get; set; }

    /// <summary>Something asked for that has not come back yet, by name ("picture", "play", "tier"…), so its button can show it.</summary>
    public string? Waiting { get; private set; }

    public bool IsWorking => Job is { State: "running" };

    public bool Advanced => settings[AppSettings.MovieAdvanced];

    // ---- The form for the next movie ----

    public string Show { get; private set; } = "General";

    public MovieKitDto? Kit { get; private set; }

    /// <summary>The next movie's title; empty lets the script writer name it.</summary>
    public string Title { get; set; } = "";

    public string Topic { get; set; } = "";

    public string Second { get; set; } = "";

    public int Number { get; set; } = 1;

    public string RoastVoice { get; set; } = "";

    public bool CrudeRoast { get; set; }

    /// <summary>How much Meme-ify adds to each film of the next movie (the sliders).</summary>
    public MemeMix Mix { get; set; } = MemeMix.Default;

    /// <summary>Who narrates the next movie, and how fast.</summary>
    public NarratorChoice Narrator { get; set; } = NarratorChoice.Default;

    public List<MovieFilm> FilmList { get; } = [];

    public MovieShowDto? ShowInfo => Config?.Shows.FirstOrDefault(s => s.Value == Show);

    // ---- Starting up ----

    /// <summary>Reads what the server offers and picks up where the user left off: the last movie, and any job still running.</summary>
    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        Config = await api.ConfigAsync();
        Prefs = await api.PrefsAsync() ?? Prefs;
        await RefreshLibraryAsync();
        await SetShowAsync(Show);
        Job = await api.JobAsync();
        if (IsWorking) Poll();
        if (await js.InvokeAsync<string?>("poMedia.read", LastKey) is { Length: > 0 } last && Library.Any(m => m.Id == last && !m.Damaged))
            await OpenAsync(last, quiet: true);
        Notify();
    }

    private CancellationTokenSource? _fading;

    public void Notify()
    {
        Fade();
        Changed?.Invoke();
    }

    /// <summary>What went well is said for ten seconds; what went wrong stays until it is closed.</summary>
    private void Fade()
    {
        if (Status == _fadingStatus) return;
        _fadingStatus = Status;
        _fading?.Cancel();
        if (Status is not { } said) return;
        var fading = _fading = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(10), fading.Token).ContinueWith(_ =>
        {
            if (Status != said) return;
            (Status, _fadingStatus) = (null, null);
            Changed?.Invoke();
        }, fading.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Current);
    }

    private string? _fadingStatus;

    public void Say(string? status, string? error = null)
    {
        (Status, Error, Notice, StatusIsInfo) = (error is null ? status : null, error, null, false);
        Notify();
    }

    /// <summary>The status is only information (nothing was done, nothing went wrong), so it is not shown as a success.</summary>
    public bool StatusIsInfo { get; private set; }

    /// <summary>Says something neutral, such as why a click changed nothing. It fades like a status.</summary>
    public void Tell(string info)
    {
        (Status, Error, Notice, StatusIsInfo) = (info, null, null, true);
        Notify();
    }

    public void Warn(string notice)
    {
        (Status, Error, Notice) = (null, null, notice);
        Notify();
    }

    public void Dismiss() => Say(null);

    public async Task RefreshLibraryAsync()
    {
        try
        {
            Library = await api.LibraryAsync();
        }
        catch (HttpRequestException)
        {
            // The list as it was is better than none.
        }
        Notify();
    }

    public async Task SetShowAsync(string show)
    {
        Show = show;
        Kit = await api.KitAsync(show);
        Number = Kit?.NextNumber ?? 1;
        // Every series starts with a spoken roast; a kids' video starts with the crude comic one.
        var voices = Config?.RoastVoices ?? [];
        RoastVoice = show == "Kids" && voices.Contains("Comic") ? "Comic" : voices.FirstOrDefault() ?? "";
        Mix = MemeMix.For(show);
        Narrator = NarratorChoice.Default;
        CrudeRoast = Mix.Crude;
        // A kids' video is the Tongtong's Toys series; a typed title stays when the kind changes.
        if (string.IsNullOrWhiteSpace(Title) || Title == KidsTitle) Title = DefaultTitle(show);
        Notify();
    }

    /// <summary>The title every kids' video starts with.</summary>
    public const string KidsTitle = "Tongtong's Toys";

    private static string DefaultTitle(string show) => show == "Kids" ? KidsTitle : "";

    public void UseKit(MovieKitDto kit)
    {
        Kit = kit;
        Notify();
    }

    public async Task SavePrefsAsync(MoviePrefsDto prefs)
    {
        Prefs = await api.SavePrefsAsync(prefs) ?? Prefs;
        Notify();
    }

    public async Task RefreshPrefsAsync()
    {
        Prefs = await api.PrefsAsync() ?? Prefs;
        Notify();
    }

    // ---- The open movie ----

    public async Task OpenAsync(string id, bool quiet = false)
    {
        var reply = await api.OpenAsync(id);
        if (reply is null)
        {
            if (!quiet) Say(null, "That movie is no longer there.");
            return;
        }
        if (reply.Movie is null)
        {
            Say(null, reply.Error == "damaged" ? "This movie's file is damaged." : reply.Error);
            return;
        }
        Take(reply.Movie);
        Playing = null;
        if (Show != reply.Movie.Show) await SetShowAsync(reply.Movie.Show);
        await js.InvokeVoidAsync("poMedia.write", LastKey, id);
        if (!quiet) Say($"Opened {reply.Movie.Title}.");
        Notify();
    }

    private void Take(MovieDto movie) => Movie = movie;

    /// <summary>Takes in what a call answered with: the movie as it now is, and what to say.</summary>
    /// <returns>True when it went through.</returns>
    public bool Apply(MovieReply reply)
    {
        if (reply.Movie is not null) Take(reply.Movie);
        if (reply.Error is not null || reply.Status is not null) (Status, Error, StatusIsInfo) = (reply.Error is null ? reply.Status : null, reply.Error, false);
        Notify();
        return reply.Error is null;
    }

    /// <summary>Makes a change to the open movie that can be undone.</summary>
    public async Task<bool> EditAsync(MovieEdit edit) => Movie is { } movie && Apply(await api.EditAsync(movie.Id, edit));

    /// <summary>
    /// Does something to the open movie that takes a moment (a picture, a play, another depth) and shows it as
    /// waiting meanwhile. A long job started this way is then followed until it ends.
    /// </summary>
    /// <param name="waiting">What to show as waiting; null for something quick.</param>
    public async Task<MovieReply?> DoAsync(string action, string? waiting = null, bool job = false)
    {
        if (Movie is not { } movie) return null;
        Waiting = waiting;
        if (waiting is not null) Notify();
        try
        {
            var reply = await api.DoAsync(movie.Id, action);
            Waiting = null;
            if (Apply(reply) && job) await FollowJobAsync();
            return reply;
        }
        finally
        {
            Waiting = null;
        }
    }

    public async Task DeleteAsync(MovieSummaryDto summary)
    {
        if (await api.DeleteAsync(summary.Id) is { } problem)
        {
            Say(null, problem);
            return;
        }
        if (Movie?.Id == summary.Id)
        {
            Movie = null;
            await js.InvokeVoidAsync("poMedia.write", LastKey, null);
        }
        await RefreshLibraryAsync();
        Say($"Deleted {summary.Title}.");
    }

    /// <summary>
    /// Has the server pack the movie's download (one zip for YouTube and TikTok), waits for it, then downloads it.
    /// </summary>
    /// <param name="beforeDownload">Run just before the download starts (the page lets go of the playing video).</param>
    /// <returns>Why it could not be made, in words fit to show; null when the download started.</returns>
    public async Task<string?> PackageAndDownloadAsync(string id, Func<Task>? beforeDownload = null)
    {
        var reply = await api.DoAsync(id, "package");
        if (reply.Error is { } refused) return refused;
        MovieJobDto? job;
        do
        {
            await Task.Delay(1000);
            try
            {
                job = await api.JobAsync();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                job = Job; // the connection dropped for a moment: the packing carries on, and so does the asking
                continue;
            }
            Job = job;
            Notify();
        }
        while (job is { State: "running" });
        if (job is not { State: "done" })
            return job?.Error ?? job?.Message ?? "The download could not be made. Try again.";
        if (beforeDownload is not null) await beforeDownload();
        await DownloadAndRemoveAsync(id);
        return null;
    }

    /// <summary>
    /// Downloads the open movie's package. The server removes the movie once the download has finished, so it is
    /// closed here, and the list read again once the download has had time to go.
    /// </summary>
    public async Task DownloadAndRemoveAsync(string id)
    {
        await js.InvokeVoidAsync("poMedia.download", $"api/movies/{Uri.EscapeDataString(id)}/download");
        if (Movie?.Id == id)
        {
            Movie = null;
            await js.InvokeVoidAsync("poMedia.write", LastKey, null);
        }
        Say("Downloading. The movie is removed from the server as soon as the download finishes.");
        await Task.Delay(TimeSpan.FromSeconds(5));
        await RefreshLibraryAsync();
    }

    /// <summary>Deletes every saved movie, one by one; any that cannot be deleted (one being worked on) are named.</summary>
    public async Task DeleteAllAsync()
    {
        var problems = new List<string>();
        var deleted = 0;
        foreach (var summary in Library.ToList())
        {
            if (await api.DeleteAsync(summary.Id) is { } problem) { problems.Add($"{summary.Title}: {problem}"); continue; }
            deleted++;
            if (Movie?.Id == summary.Id)
            {
                Movie = null;
                await js.InvokeVoidAsync("poMedia.write", LastKey, null);
            }
        }
        await RefreshLibraryAsync();
        Say(deleted == 0 ? null : deleted == 1 ? "Deleted 1 movie." : $"Deleted {deleted} movies.", problems.Count > 0 ? string.Join(" ", problems) : null);
    }

    // ---- The long job ----

    /// <summary>Starts a new movie's script, or the whole video from the films.</summary>
    /// <summary>What a new movie is made from: the form's fields as they are now (checked by <c>MovieLessonRequestValidator</c>).</summary>
    public MovieLessonRequest LessonRequest() => new(Show, Kit?.Structure ?? "", Topic, Second, Number,
        [.. FilmList.Select(f => new MovieFilmDto(f.Media.Id, f.Note, f.OwnSound))], Prefs.ExportPreset,
        string.IsNullOrWhiteSpace(RoastVoice) ? null : RoastVoice, Mix.Crude, string.IsNullOrWhiteSpace(Title) ? null : Title.Trim(), Mix.Encode(), Narrator.Encode());

    public async Task StartMovieAsync(bool whole)
    {
        var reply = await api.StartAsync(LessonRequest(), whole);
        if (reply.Error is not null)
        {
            Say(null, reply.Error);
            return;
        }
        Say(null);
        await FollowJobAsync();
    }

    /// <summary>Reads the job that was just started and keeps reading it until it ends.</summary>
    public async Task FollowJobAsync()
    {
        Job = await api.JobAsync();
        Notify();
        if (IsWorking) Poll();
        else if (Job is not null) await EndedAsync(Job);
    }

    private void Poll()
    {
        _polling?.Cancel();
        var polling = _polling = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!polling.IsCancellationRequested)
            {
                await Task.Delay(700);
                MovieJobDto? job;
                try
                {
                    job = await api.JobAsync();
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    continue; // the connection dropped for a moment: the job carries on, and so does the asking
                }
                if (polling.IsCancellationRequested) return;
                Job = job;
                if (job is not { State: "running" })
                {
                    if (job is not null) await EndedAsync(job);
                    return;
                }
                Notify();
            }
        });
    }

    private async Task EndedAsync(MovieJobDto job)
    {
        // A movie that was published is removed from the server by then: close it here too.
        if (job is { Title: "Publishing", State: "done", MovieId: null } && Movie is not null)
        {
            Movie = null;
            await js.InvokeVoidAsync("poMedia.write", LastKey, null);
        }
        // The movie the job made or changed is read afresh. A new one clears the form for the next.
        if (job.MovieId is { } id)
        {
            var opening = Movie?.Id != id;
            if (await api.OpenAsync(id) is { Movie: { } movie })
            {
                Take(movie);
                if (opening)
                {
                    FilmList.Clear();
                    (Title, Number) = (DefaultTitle(Show), movie.Number + 1);
                    await js.InvokeVoidAsync("poMedia.write", LastKey, id);
                }
            }
        }
        // A job can end with something made and something to put right: both are said, together.
        (Status, StatusIsInfo) = (job.Error is null ? job.Message : null, false);
        Error = job.Error is null ? null : job.Message is null ? job.Error : $"{job.Message} {job.Error}";
        await js.InvokeVoidAsync("poMedia.notifyDone", job.State == "done" ? "Finished" : "Stopped", job.Message ?? job.Error ?? job.Title);
        _ = RefreshLibraryAsync();
        _ = RefreshPrefsAsync();
        Notify();
    }

    public async Task CancelJobAsync()
    {
        await api.CancelJobAsync();
        Notify();
    }

    public bool IntroWatched { get; set; }

    public void Dispose()
    {
        _polling?.Cancel();
        _polling?.Dispose();
        _fading?.Cancel();
        _fading?.Dispose();
    }
}
