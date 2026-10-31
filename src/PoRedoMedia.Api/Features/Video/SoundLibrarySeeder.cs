namespace PoRedoMedia.Api.Features.Video;

/// <summary>
/// Fills the meme sound library from <c>meme-sounds/sounds-metadata.json</c> (copied beside the
/// app) once per start, in the background, and removes library sounds no longer listed. Sounds already in storage are skipped, so after the first
/// start this is a quick check; one that cannot be fetched is tried again next start. Not in Test,
/// whose storage is throwaway and must not reach the internet.
/// </summary>
public sealed partial class SoundLibrarySeeder(IConfiguration configuration, IHostEnvironment environment, ILogger<SoundLibrarySeeder> logger)
    : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Sound library: {Seeded} added, {Skipped} already there, {Removed} removed, {Failed} could not be fetched")]
    private partial void LogSeeded(int seeded, int skipped, int removed, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sound library seeding failed; it is tried again next start")]
    private partial void LogFailed(Exception ex);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsEnvironment(PoEnvironments.Test))
            return;

        // Let the app finish starting before the downloads begin.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        try
        {
            var result = await SeedSoundsCommand.RunForDirAsync(Path.Combine(AppContext.BaseDirectory, "meme-sounds"), configuration, verbose: false);
            LogSeeded(result.Seeded, result.Skipped, result.Removed, result.Failed);
        }
        catch (Exception ex) when (ex is Azure.RequestFailedException or HttpRequestException or IOException)
        {
            LogFailed(ex);
        }
    }
}
