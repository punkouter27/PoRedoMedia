using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Features.Housekeeping;
using PoRedoMedia.Api.Features.Runs;
using PoRedoMedia.Api.Features.Sharing;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.IntegrationTests;

[Collection(AzuriteCollection.Name)]
public sealed class HousekeepingTests(AzuriteFixture azurite)
{
    [DockerFact]
    public async Task One_sweep_recovers_a_lost_run_deletes_only_what_is_expired_and_unpinned_and_drops_retired_storage()
    {
        using var factory = new AppFactory(azurite.ConnectionString);
        var services = factory.Services;
        var (media, runs, blobs, links, quota) = (
            services.GetRequiredService<IMediaRepository>(), services.GetRequiredService<IRunRepository>(),
            services.GetRequiredService<BlobStorageService>(), services.GetRequiredService<ShareLinkStore>(),
            services.GetRequiredService<IRenderQuota>());
        var owner = new UserId($"dev|{Guid.NewGuid()}");

        async Task<MediaItem> AddAsync(int ageDays, bool pinned = false, string? shareToken = null)
        {
            var item = new MediaItem
            {
                Owner = owner, Id = MediaId.New(), Kind = MediaKind.Image, Status = MediaStatus.Ready, Origin = "Upload", Title = "t",
                ContentType = "image/png", Extension = ".png", Pinned = pinned, ShareToken = shareToken, CreatedAt = DateTimeOffset.UtcNow.AddDays(-ageDays),
            };
            await blobs.UploadAsync(item.SourcePath, [1, 2, 3], "image/png");
            await media.SaveAsync(item);
            if (shareToken is not null)
                await links.AddAsync(shareToken, owner, item.Id, default);
            return item;
        }

        var token = ShareLinkStore.NewToken();
        var expired = await AddAsync(ageDays: 31, shareToken: token);
        var pinned = await AddAsync(ageDays: 400, pinned: true);
        var recent = await AddAsync(ageDays: 1);

        // Storage older versions wrote and nothing reads any more.
        var storage = services.GetRequiredService<StorageClients>();
        var script = $"{MediaBlobPaths.Prefix(recent.Id)}script.json";
        await blobs.UploadAsync(script, "{}"u8.ToArray(), "application/json");
        storage.Table(HousekeepingService.RetiredFavoritesTable);

        // Saved after the dispatcher's own recovery, so only the sweep below can find it.
        await services.GetRequiredService<RunDispatcher>().Recovered;

        // A run left "Running" by a process that no longer exists, which had cost a credit.
        await quota.TryConsumeAsync(owner);
        var lost = new Run
        {
            Owner = owner, Id = RunId.New(), SourceId = recent.Id, Functions = [MediaFunction.MemeCaption],
            Status = RunStatus.Running, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        await runs.SaveAsync(lost);

        var sweeper = new HousekeepingService(
            runs, media, blobs, links, quota, services.GetRequiredService<RunDispatcher>(), storage,
            new ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<HousekeepingService>.Instance);
        await sweeper.SweepAsync(default);

        var after = await runs.GetAsync(owner, lost.Id);
        Assert.Equal(RunStatus.Failed, after!.Status);
        Assert.Contains("Interrupted by a server restart", after.Error);
        Assert.Equal(0, (await quota.GetStatusAsync(owner)).Used);

        Assert.Null(await media.GetAsync(owner, expired.Id));
        Assert.False(await blobs.ExistsAsync(expired.SourcePath));
        Assert.Null(await links.ResolveAsync(token, default));
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync($"/v/{token}")).StatusCode);
        Assert.NotNull(await media.GetAsync(owner, pinned.Id));
        Assert.NotNull(await media.GetAsync(owner, recent.Id));

        Assert.False(await blobs.ExistsAsync(script));
        Assert.True(await blobs.ExistsAsync(recent.SourcePath));
        Assert.False(await storage.DeleteTableIfExistsAsync(HousekeepingService.RetiredFavoritesTable));
    }
}
