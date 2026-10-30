using PoRedoMedia.Api.Features.Runs;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Housekeeping;

/// <summary>
/// The periodic sweep. It recovers runs a restart cut short, removes uploads that never finished,
/// and deletes items past their retention unless they are pinned. The first sweep of a process
/// also removes storage that older versions wrote and nothing reads any more.
/// </summary>
/// <remarks>
/// Done in the app, not with a storage lifecycle rule, because a rule cannot honour pins or remove
/// the table rows and share links that go with an item.
/// </remarks>
public sealed class HousekeepingService(
    IRunRepository runs, IMediaRepository media, BlobStorageService blobs, IShareLinks links, IRenderQuota quota,
    RunDispatcher dispatcher, StorageClients storage, IConfiguration configuration, TimeProvider time,
    ILogger<HousekeepingService> logger) : BackgroundService
{
    public const int DefaultRetentionDays = 30;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AbandonedUploadAge = TimeSpan.FromHours(24);

    /// <summary>The sound favourites table, dropped with the Sounds page.</summary>
    internal const string RetiredFavoritesTable = "SoundFavorites";

    /// <summary>The director script once kept beside each video (<c>media/{id}/script.json</c>).</summary>
    internal const string RetiredScriptBlob = "/script.json";

    private readonly DateTimeOffset _startedAt = time.GetUtcNow();
    private bool _retiredRemoved;

    public enum Verdict
    {
        Keep,
        DeleteAbandonedUpload,
        DeleteExpired,
    }

    /// <summary>What the sweep does with one item. A retention of zero or less keeps everything.</summary>
    public static Verdict Classify(MediaItem item, DateTimeOffset now, int retentionDays) =>
        item.Status == MediaStatus.Uploading
            ? now - item.CreatedAt > AbandonedUploadAge ? Verdict.DeleteAbandonedUpload : Verdict.Keep
            : retentionDays > 0 && !item.Pinned && now - item.CreatedAt > TimeSpan.FromDays(retentionDays)
                ? Verdict.DeleteExpired
                : Verdict.Keep;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!storage.IsConfigured || configuration.GetValue<bool?>(ConfigKeys.HousekeepingEnabled) == false)
            return;

        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Housekeeping sweep failed; it runs again in {Minutes} minutes", Interval.TotalMinutes);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // The dispatcher queues interrupted runs again first. One still marked unfinished from
        // before this process started, and not queued, could not be: it is failed with a reason,
        // and today's credit for it is given back.
        await dispatcher.Recovered.WaitAsync(ct);
        foreach (var run in await runs.ListUnfinishedAsync(ct))
        {
            if (run.CreatedAt >= _startedAt || dispatcher.IsBusy(run.SourceId))
                continue;

            await runs.SaveAsync(run with { Status = RunStatus.Failed, Error = "Interrupted by a server restart. Run it again; it was not charged." }, ct);
            if (run.CreatedAt.UtcDateTime.Date == now.UtcDateTime.Date)
                await quota.RefundAsync(run.Owner, ct);
        }

        var retentionDays = configuration.GetValue<int?>(ConfigKeys.RetentionDays) ?? DefaultRetentionDays;
        foreach (var item in await media.ListAllAsync(ct))
        {
            if (Classify(item, now, retentionDays) == Verdict.Keep || dispatcher.IsBusy(item.Id))
                continue;

            if (item.ShareToken is not null)
                await links.RevokeAsync(item.ShareToken, ct);
            await blobs.DeletePrefixAsync(MediaBlobPaths.Prefix(item.Id), ct);
            await media.DeleteAsync(item.Owner, item.Id, ct);
        }

        if (!_retiredRemoved)
        {
            await RemoveRetiredStorageAsync(ct);
            _retiredRemoved = true;
        }
    }

    /// <summary>
    /// Deletes what older versions stored and this one never reads. Cheap once it has run: the
    /// table is gone and no blob matches, so later processes only list the container.
    /// </summary>
    // ponytail: once every deployed account has been swept, this and the two names above can go.
    internal async Task RemoveRetiredStorageAsync(CancellationToken ct)
    {
        if (await storage.DeleteTableIfExistsAsync(RetiredFavoritesTable, ct))
            logger.LogInformation("Dropped the retired {Table} table", RetiredFavoritesTable);

        var scripts = 0;
        await foreach (var path in blobs.ListBlobsByPrefixAsync($"{StorageNames.Containers.Media}/", ct))
        {
            if (path.EndsWith(RetiredScriptBlob, StringComparison.Ordinal))
            {
                await storage.Blob(path).DeleteIfExistsAsync(cancellationToken: ct);
                scripts++;
            }
        }

        if (scripts > 0)
            logger.LogInformation("Deleted {Count} retired script.json blob(s)", scripts);
    }
}
