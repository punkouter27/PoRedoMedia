namespace PoRedoMedia.Api.Features.Movies;

/// <summary>
/// No video is kept on this server: a movie leaves when it is downloaded (<see cref="MovieStudio.DownloadOf"/>). This
/// removes the ones never downloaded, and any films left staged, once nothing has touched them for <see cref="MaxIdle"/>.
/// A user with a job under way is skipped.
/// </summary>
public sealed partial class MovieSweeper(MovieEngine engine, MovieStudios studios, TimeProvider time, ILogger<MovieSweeper> logger) : BackgroundService
{
    public static readonly TimeSpan MaxIdle = TimeSpan.FromHours(3);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(20);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} movie folders left on the server")]
    private partial void LogRemoved(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Movie sweep failed")]
    private partial void LogFailed(Exception e);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                if (Sweep(engine.Root, time.GetUtcNow().UtcDateTime, studios.IsWorkingIn) is > 0 and var count)
                    LogRemoved(count);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LogFailed(e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Deletes each movie and staged film under <paramref name="root"/> untouched for <see cref="MaxIdle"/>.</summary>
    /// <returns>How many were deleted.</returns>
    public static int Sweep(string root, DateTime nowUtc, Func<string, bool> isWorkingIn)
    {
        if (!Directory.Exists(root)) return 0;
        var removed = 0;
        foreach (var home in Directory.EnumerateDirectories(root))
        {
            if (isWorkingIn(home)) continue;
            foreach (var parent in new[] { Path.Combine(home, "episodes"), Path.Combine(home, "staging") }.Where(Directory.Exists))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
                {
                    if (nowUtc - LastTouched(entry) < MaxIdle) continue;
                    try
                    {
                        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                        else File.Delete(entry);
                        removed++;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // Still open; the next sweep tries again.
                    }
                }
            }
        }
        return removed;
    }

    private static DateTime LastTouched(string entry) =>
        Directory.Exists(entry)
            ? new DirectoryInfo(entry).EnumerateFiles("*", SearchOption.AllDirectories).Select(f => f.LastWriteTimeUtc)
                .Append(Directory.GetLastWriteTimeUtc(entry)).Max()
            : File.GetLastWriteTimeUtc(entry);
}
