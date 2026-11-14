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

/// <summary>One line of the checklist under the step bar.</summary>
public sealed record MovieGuideItem(string Text, bool Done, bool Optional = false);

/// <summary>
/// What Create Movie holds while it is open: the server's choices, the open movie, the form for the next one, the
/// long job under way and the last thing said. The pages draw from it and ask it to act; it raises
/// <see cref="Changed"/> whenever what they show has moved on.
/// </summary>
public sealed class MovieStore(MovieApi api, IJSRuntime js, AppSettings settings) : IDisposable
{
    public const string Template = "template", Films = "films", Clips = "clips", Preview = "preview", Video = "video";

    private const string LastKey = "poredomedia:movie:last";
    private CancellationTokenSource? _polling;
    private bool _started;
    private bool _chosen;

    public event Action? Changed;

    public MovieConfigDto? Config { get; private set; }

    public MovieDto? Movie { get; private set; }

    public MovieJobDto? Job { get; private set; }

    public List<MovieSummaryDto> Library { get; private set; } = [];

    public MoviePrefsDto Prefs { get; private set; } = new(0, false, "Hd30", 0);

    public string Step { get; private set; } = Films;

    public string? Status { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Something worth knowing that is not a failure (a film could not be heard); stays until it is closed.</summary>
    public string? Notice { get; private set; }

    /// <summary>The selected clip on the Voice-over step.</summary>
    public Guid? Selected { get; set; }

    /// <summary>A small video to play over the page: a clip or a teaser, watched on its own.</summary>
    public (string Url, bool Upright)? Playing { get; set; }

    /// <summary>Something asked for that has not come back yet, by name ("picture", "play", "tier"…), so its button can show it.</summary>
    public string? Waiting { get; private set; }

    public bool IsWorking => Job is { State: "running" };

    public bool Advanced => settings[AppSettings.MovieAdvanced];

    // ---- The form for the next movie ----

    public string Show { get; private set; } = "General";

    public MovieKitDto? Kit { get; private set; }

    public string Topic { get; set; } = "";

    public string Second { get; set; } = "";

    public int Number { get; set; } = 1;

    public List<MovieFilm> FilmList { get; } = [];

    /// <summary>True while the form for a new movie shows on the Films step in place of the open movie's own.</summary>
    public bool StartingNew { get; set; } = true;

    public MovieShowDto? ShowInfo => Config?.Shows.FirstOrDefault(s => s.Value == Show);

    public MovieClipDto? SelectedClip => Movie?.Clips.FirstOrDefault(c => c.Id == Selected) ?? Movie?.Clips.FirstOrDefault();

    // ---- Starting up ----

    /// <summary>Reads what the server offers and picks up where the user left off: the last movie, and any job still running.</summary>
    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        settings.Changed += OnSettings;
        Config = await api.ConfigAsync();
        Prefs = await api.PrefsAsync() ?? Prefs;
        await RefreshLibraryAsync();
        await SetShowAsync(Show);
        Job = await api.JobAsync();
        if (IsWorking) Poll();
        if (await js.InvokeAsync<string?>("poMedia.read", LastKey) is { Length: > 0 } last && Library.Any(m => m.Id == last && !m.Damaged))
            await OpenAsync(last, quiet: true);
        // A step the user has already gone to while this was loading is where they stay.
        if (Movie is null && !_chosen)
            Step = Kit is { ChannelName.Length: > 0 } ? Films : Template;
        Notify();
    }

    private void OnSettings()
    {
        // The quick Preview step is an advanced one: with advanced controls off, it gives way to Video.
        if (!Advanced && Step == Preview) Step = Video;
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
        (Status, Error, Notice) = (error is null ? status : null, error, null);
        Notify();
    }

    public void Warn(string notice)
    {
        (Status, Error, Notice) = (null, null, notice);
        Notify();
    }

    public void Dismiss() => Say(null);

    /// <summary>Goes to a step. One that needs a movie lands on Films when there is none; Preview lands on Video unless advanced controls are on.</summary>
    public void GoTo(string step)
    {
        if (step is Clips or Preview or Video && Movie is null) step = Films;
        if (step == Preview && !Advanced) step = Video;
        _chosen = true;
        if (step == Step) return;
        (Step, Error) = (step, null);
        if (step == Films) _ = RefreshLibraryAsync();
        Notify();
    }

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
        Notify();
    }

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
        (Selected, StartingNew, Playing) = (reply.Movie.Clips.FirstOrDefault()?.Id, false, null);
        if (Show != reply.Movie.Show) await SetShowAsync(reply.Movie.Show);
        await js.InvokeVoidAsync("poMedia.write", LastKey, id);
        if (!quiet)
        {
            Step = Clips;
            Say($"Opened {reply.Movie.Title}.");
        }
        else if (Step is Films or Template && !_chosen)
        {
            Step = Clips;
        }
        Notify();
    }

    private void Take(MovieDto movie)
    {
        Movie = movie;
        if (movie.Clips.All(c => c.Id != Selected)) Selected = movie.Clips.FirstOrDefault()?.Id;
    }

    /// <summary>Takes in what a call answered with: the movie as it now is, and what to say.</summary>
    /// <returns>True when it went through.</returns>
    public bool Apply(MovieReply reply)
    {
        if (reply.Movie is not null) Take(reply.Movie);
        if (reply.Error is not null || reply.Status is not null) (Status, Error) = (reply.Error is null ? reply.Status : null, reply.Error);
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

    public async Task<bool> SetDialogueAsync(Guid clip, string words)
    {
        if (Movie is not { } movie) return false;
        Waiting = "words";
        Notify();
        try
        {
            return Apply(await api.DialogueAsync(movie.Id, clip, words));
        }
        finally
        {
            Waiting = null;
            Notify();
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
            (Movie, Selected, StartingNew, Step) = (null, null, true, Films);
            await js.InvokeVoidAsync("poMedia.write", LastKey, null);
        }
        await RefreshLibraryAsync();
        Say($"Deleted {summary.Title}.");
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
                (Movie, Selected, StartingNew, Step) = (null, null, true, Films);
                await js.InvokeVoidAsync("poMedia.write", LastKey, null);
            }
        }
        await RefreshLibraryAsync();
        Say(deleted == 0 ? null : deleted == 1 ? "Deleted 1 movie." : $"Deleted {deleted} movies.", problems.Count > 0 ? string.Join(" ", problems) : null);
    }

    // ---- The long job ----

    /// <summary>Starts a new movie's script, or the whole video from the films.</summary>
    public async Task StartMovieAsync(bool whole)
    {
        var request = new MovieLessonRequest(Show, Kit?.Structure ?? "", Topic, Second, Number,
            [.. FilmList.Select(f => new MovieFilmDto(f.Media.Id, f.Note, f.OwnSound))], Prefs.ExportPreset);
        var reply = await api.StartAsync(request, whole);
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
        // The movie the job made or changed is read afresh, then the step it leads to is shown.
        if (job.MovieId is { } id)
        {
            var opening = Movie?.Id != id;
            if (await api.OpenAsync(id) is { Movie: { } movie })
            {
                Take(movie);
                if (opening)
                {
                    (Selected, StartingNew) = (movie.Clips.FirstOrDefault()?.Id, false);
                    FilmList.Clear();
                    await js.InvokeVoidAsync("poMedia.write", LastKey, id);
                }
            }
        }
        if (job.State == "done" && job.GoTo is { } step && Movie is not null)
            Step = step == Preview && !Advanced ? Video : step;
        // A job can end with something made and something to put right: both are said, together.
        Status = job.Error is null ? job.Message : null;
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

    // ---- The checklist ----

    /// <summary>What the step in view asks for, in order. The first that is neither done nor optional is what to do next.</summary>
    public IReadOnlyList<MovieGuideItem> Guide()
    {
        var movie = Movie;
        switch (Step)
        {
            case Template:
                return [new("Channel name", Kit is { ChannelName.Length: > 0 }), new("Watch the intro", IntroWatched, Optional: true)];
            case Films:
                var open = movie is not null && !StartingNew;
                var films = open ? movie!.Clips.Count(c => c.HasFilm) : FilmList.Count;
                return
                [
                    new(ShowInfo?.TopicLabel ?? "What it is about", open || Topic.Trim().Length > 0),
                    new(films > 0 ? $"Films · {films}" : "Films", films > 0),
                    new("Write the script", open),
                ];
            case Clips when movie is not null:
                var items = new List<MovieGuideItem> { new($"Voices · {movie.Clips.Count(c => c.Measured || c.OwnSound)} of {movie.Clips.Length}", movie.Clips.All(c => c.Measured || c.OwnSound)) };
                // Every clip in the middle is one of the user's own videos.
                items.Add(new(movie.Clips.All(c => c.HasFilm) ? "Your videos" : $"Videos · {movie.Clips.Count(c => !c.HasFilm)} clip(s) without one", movie.Clips.All(c => c.HasFilm)));
                var over = movie.RuntimeSeconds > (Config?.MaxMovieSeconds ?? 600);
                items.Add(new($"Length {TimeSpan.FromSeconds(movie.RuntimeSeconds):m\\:ss}{(over ? " · over 10:00" : "")}", !over, Optional: !over));
                if (movie.Clips.Count(c => c.Warnings.Length > 0) is > 0 and var toCheck)
                    items.Add(new(toCheck == 1 ? "1 clip to check" : $"{toCheck} clips to check", false, Optional: true));
                return items;
            case Preview when movie is not null:
                return [new("Build the preview", movie.Preview is not null), new("Outro music", movie.MusicName is not null, Optional: true)];
            case Video when movie is not null:
                return [new("Teasers", movie.Publish.TeaserUrls.Length > 0, Optional: true), new("Render the finished video", movie.Publish.MasterUrl is not null)];
            default:
                return [];
        }
    }

    public bool IntroWatched { get; set; }

    public void Dispose()
    {
        settings.Changed -= OnSettings;
        _polling?.Cancel();
        _polling?.Dispose();
        _fading?.Cancel();
        _fading?.Dispose();
    }
}
