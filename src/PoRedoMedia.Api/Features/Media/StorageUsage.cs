using PoRedoMedia.Api.Features.Movies;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Media;

/// <summary>
/// A user's storage against their allowance (<see cref="ConfigKeys.StorageUserLimitMb"/>): the blobs under each of
/// their Library items (sources, thumbnails, exports, run leftovers) plus their Create Movie files on disk.
/// Uploads that would go over it are refused.
/// </summary>
public sealed class StorageUsage(IMediaRepository media, BlobStorageService blobs, MovieStudios movies, IConfiguration configuration)
{
    private const long DefaultUserLimitMb = 5120;

    public long LimitBytes { get; } = (configuration.GetValue<long?>(ConfigKeys.StorageUserLimitMb) ?? DefaultUserLimitMb) * 1024 * 1024;

    public async Task<StorageUsageDto> MeasureAsync(UserId user, CancellationToken ct = default)
    {
        var items = await media.ListAsync(user, ct);
        var sizes = await Task.WhenAll(items.Select(m => blobs.SizeOfPrefixAsync(MediaBlobPaths.Prefix(m.Id), ct)));
        var movieBytes = movies.For(user).DiskBytes();
        return new StorageUsageDto(sizes.Sum() + movieBytes, LimitBytes, items.Count(m => m.Status == MediaStatus.Ready), movieBytes);
    }

    /// <summary>Why <paramref name="adding"/> more bytes would not fit, or null when it does.</summary>
    public static string? WouldOverflow(StorageUsageDto usage, long adding) =>
        usage.UsedBytes + adding <= usage.LimitBytes
            ? null
            : $"Your storage is full ({Size(usage.LeftBytes)} left of {Size(usage.LimitBytes)}). Delete items in the Library to make room.";

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}
