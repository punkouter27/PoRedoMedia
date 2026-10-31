using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoRedoMedia.Api.Features.Video;

/// <summary>
/// CLI seeder: dotnet run -- seed-sounds [--seeds-dir &lt;path&gt;]
/// Reads scripts/meme-sounds/sounds-metadata.json and uploads MP3s to Blob Storage, then writes
/// SoundAsset rows to Table Storage. Any clip missing from disk is fetched from its sourceUrl, so
/// this bootstraps the library from a bare clone. Idempotent — skips rows already seeded locally.
/// </summary>
public static class SeedSoundsCommand
{
    internal const string ContainerName = StorageNames.Containers.Sounds;
    internal const string TableName = StorageNames.Tables.SoundAssets;
    internal const string PartitionKey = "library";

    public static async Task<int> RunAsync(string[] args, IConfiguration config)
    {
        var seedsDir = ResolveSeedsDir(args, Directory.GetCurrentDirectory());
        var result = await RunForDirAsync(seedsDir, config, verbose: true);
        return result is { Failed: 0 } ? 0 : 1;
    }

    /// <summary>
    /// Library-loading path used by both the CLI verb and <see cref="SoundLibrarySeeder"/> at startup.
    /// Resolves <c>seedsDir</c> relative to <paramref name="contentRoot"/> so the running web host
    /// can find the metadata no matter the working directory.
    /// </summary>
    public static async Task<SeedResult> RunForDirAsync(string seedsDir, IConfiguration config, bool verbose)
    {
        var metaFile = Path.Combine(seedsDir, "sounds-metadata.json");

        if (verbose)
        {
            Console.WriteLine("╔══════════════════════════════════════════════════╗");
            Console.WriteLine("║  PoRedoMedia // SOUND LIBRARY SEEDER             ║");
            Console.WriteLine("╚══════════════════════════════════════════════════╝");
            Console.WriteLine();
        }

        if (!File.Exists(metaFile))
        {
            if (verbose)
            {
                Console.Error.WriteLine($"✗ Metadata file not found: {metaFile}");
                Console.Error.WriteLine("  Pass --seeds-dir <path>, or run from the repository root.");
            }
            return new SeedResult(0, 0, 1, 0);
        }

        // ── Parse metadata ───────────────────────────────────────────────────
        var json = await File.ReadAllTextAsync(metaFile);
        var meta = JsonSerializer.Deserialize<SoundsMetadata>(json, JsonOptions)!;
        if (verbose)
        {
            Console.WriteLine($"  Found {meta.Sounds.Count} sounds in metadata.");
            Console.WriteLine();
        }

        // ── Connect to storage ───────────────────────────────────────────────
        // Through the app's own factories, so the seeder writes where the app reads. It used to
        // read ConnectionStrings:AzureStorage, a key nothing sets, and so always fell back to
        // Azurite whatever the configuration said. Both factories create what is missing.
        var containerClient = new StorageClients(config)
            .Container(ContainerName);
        if (verbose) Console.WriteLine($"  ✓ Blob container '{ContainerName}' ready.");

        var tableClient = new StorageClients(config).Table(TableName);
        if (verbose) Console.WriteLine($"  ✓ Table '{TableName}' ready.");
        if (verbose) Console.WriteLine();

        int seeded = 0, skipped = 0, failed = 0;
        var listed = meta.Sounds.Select(e => DeriveStableGuid(e.Id ?? e.Filename).ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The list is in popularity order: its position is the sound's rank (1 = most famous).
        foreach (var (entry, rank) in meta.Sounds.Select((e, i) => (e, i + 1)))
        {
            var soundId = DeriveStableGuid(entry.Id ?? entry.Filename);

            // Skip a sound already seeded into this account: its row points at this container's
            // blob, the blob is there, its length is known and a local copy (if any) is the same size.
            try
            {
                var existing = await tableClient.GetEntityAsync<TableEntity>(PartitionKey, soundId.ToString());
                var bClient = containerClient.GetBlobClient(entry.Filename);
                var localFileForCheck = Path.Combine(seedsDir, entry.Filename);
                if ((existing.Value.GetInt32("DurationMs") ?? 0) > 0
                    && string.Equals(existing.Value.GetString("BlobUrl"), bClient.Uri.ToString(), StringComparison.OrdinalIgnoreCase)
                    && await bClient.ExistsAsync()
                    && (!File.Exists(localFileForCheck)
                        || (await bClient.GetPropertiesAsync()).Value.ContentLength == new FileInfo(localFileForCheck).Length))
                {
                    // The clip is in place; a changed name, tags, hint or rank only rewrites the row.
                    var row = existing.Value;
                    if (row.GetString("DisplayName") != entry.DisplayName || row.GetString("Tags") != string.Join(",", entry.ActionVectorTags)
                        || row.GetString("UseCase") != entry.UseCase || row.GetInt32("Popularity") != rank || row.GetBoolean("Priority") != entry.Priority)
                    {
                        await tableClient.UpsertEntityAsync(new TableEntity(PartitionKey, soundId.ToString())
                        {
                            ["DisplayName"] = entry.DisplayName,
                            ["Tags"] = string.Join(",", entry.ActionVectorTags),
                            ["UseCase"] = entry.UseCase,
                            ["Popularity"] = rank,
                            ["Priority"] = entry.Priority,
                        }, TableUpdateMode.Merge);
                    }

                    if (verbose) Console.WriteLine($"  [SKIP] {entry.DisplayName}");
                    skipped++;
                    continue;
                }
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                // Not found — proceed with seed
            }

            // Upload blob: local file preferred, source URL fallback
            var localFile = Path.Combine(seedsDir, entry.Filename);
            string blobUrl;

            if (File.Exists(localFile))
            {
                var localLen = new FileInfo(localFile).Length;
                var blobClient = containerClient.GetBlobClient(entry.Filename);
                var bExists = await blobClient.ExistsAsync();
                var needsUpload = !bExists;
                if (bExists)
                {
                    var p = await blobClient.GetPropertiesAsync();
                    if (p.Value.ContentLength != localLen)
                    {
                        needsUpload = true;
                    }
                }
                if (needsUpload)
                {
                    await using var stream = File.OpenRead(localFile);
                    await blobClient.UploadAsync(stream, new BlobHttpHeaders
                    {
                        ContentType = "audio/mpeg"
                    });
                }
                blobUrl = blobClient.Uri.ToString();
            }
            else if (!string.IsNullOrWhiteSpace(entry.SourceUrl))
            {
                // Self-host: download from source URL into our blob container. Keeps the stream
                // endpoint self-contained (no outbound proxy required at runtime).
                var blobClient = containerClient.GetBlobClient(entry.Filename);
                if (!await blobClient.ExistsAsync())
                {
                    try
                    {
                        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                        using var req = new HttpRequestMessage(HttpMethod.Get, entry.SourceUrl);
                        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (PoRedoMedia sound seeder)");
                        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                        if (resp.IsSuccessStatusCode)
                        {
                            await using var stream = await resp.Content.ReadAsStreamAsync();
                            await blobClient.UploadAsync(stream, new BlobHttpHeaders { ContentType = "audio/mpeg" });
                        }
                        else if (verbose)
                        {
                            Console.WriteLine($"  [WARN] HTTP {(int)resp.StatusCode} fetching {entry.SourceUrl}");
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                    {
                        if (verbose) Console.WriteLine($"  [WARN] Could not download {entry.SourceUrl}: {ex.Message}");
                    }
                }

                // A clip that could not be fetched is left out (and tried again next time), never
                // stood in for by a placeholder that would play as noise.
                if (!await blobClient.ExistsAsync() || (await blobClient.GetPropertiesAsync()).Value.ContentLength < 1000)
                {
                    await blobClient.DeleteIfExistsAsync();
                    failed++;
                    continue;
                }

                blobUrl = blobClient.Uri.ToString();
            }
            else
            {
                if (verbose) Console.WriteLine($"  [WARN] MP3 + sourceUrl both missing: {entry.Filename}");
                failed++;
                continue;
            }

            // The manifest carries no real durations (0 throughout), so measure the clip itself.
            var durationMs = entry.DurationMs > 0
                ? entry.DurationMs
                : await MeasureDurationMsAsync(localFile, containerClient.GetBlobClient(entry.Filename));

            // Insert (or upsert) table row — even on refresh we rewrite the row so the BlobUrl
            // always points at our local container.
            var entity = new TableEntity(PartitionKey, soundId.ToString())
            {
                ["DisplayName"] = entry.DisplayName,
                ["DurationMs"] = durationMs,
                ["Tags"] = string.Join(",", entry.ActionVectorTags),
                ["BlobUrl"] = blobUrl,
                ["Priority"] = entry.Priority,
                ["UseCase"] = entry.UseCase,
                ["Popularity"] = rank,
            };

            try
            {
                await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
                if (verbose) Console.WriteLine($"  [SEED] {entry.DisplayName}");
                seeded++;
            }
            catch (Azure.RequestFailedException ex)
            {
                if (verbose) Console.WriteLine($"  [FAIL] {entry.DisplayName}: {ex.Message}");
                failed++;
            }
        }

        // Library sounds dropped from the list leave storage too: the list is the whole shared
        // library. A user's own sound (one with an Owner) is never the list's to remove.
        var removed = 0;
        await foreach (var row in tableClient.QueryAsync<TableEntity>(r => r.PartitionKey == PartitionKey, select: ["RowKey", "BlobUrl", "Owner"]))
        {
            if (listed.Contains(row.RowKey) || !string.IsNullOrEmpty(row.GetString("Owner")))
                continue;
            if (row.GetString("BlobUrl") is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var blobUri))
                await containerClient.GetBlobClient(Uri.UnescapeDataString(blobUri.Segments[^1])).DeleteIfExistsAsync();
            await tableClient.DeleteEntityAsync(PartitionKey, row.RowKey);
            if (verbose) Console.WriteLine($"  [DROP] {row.RowKey}");
            removed++;
        }

        if (verbose)
        {
            Console.WriteLine();
            Console.WriteLine($"╔══════════════════════════════════════════════════╗");
            Console.WriteLine($"║  DONE — Seeded: {seeded,4} | Skipped: {skipped,4} | Failed: {failed,4}  ║");
            Console.WriteLine($"╚══════════════════════════════════════════════════╝");
        }

        return new SeedResult(seeded, skipped, failed, removed);
    }

    /// <summary>Reads the clip (local copy first, else the uploaded blob) and measures it; 0 if unreadable.</summary>
    private static async Task<int> MeasureDurationMsAsync(string localFile, BlobClient blob)
    {
        try
        {
            var bytes = File.Exists(localFile)
                ? await File.ReadAllBytesAsync(localFile)
                : (await blob.DownloadContentAsync()).Value.Content.ToArray();
            return AudioDuration.EstimateMs(bytes, localFile);
        }
        catch (Exception ex) when (ex is IOException or Azure.RequestFailedException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Locates the directory containing <c>sounds-metadata.json</c>: the explicit override, else
    /// <c>scripts/meme-sounds</c> from the repository root or from the API project folder.
    /// </summary>
    public static string ResolveSeedsDir(string[] args, string cwd)
    {
        var explicitDir = GetArg(args, "--seeds-dir");
        if (!string.IsNullOrWhiteSpace(explicitDir))
            return explicitDir!;

        var candidates = new[]
        {
            Path.Combine(cwd, "scripts", "meme-sounds"),
            Path.GetFullPath(Path.Combine(cwd, "..", "..", "scripts", "meme-sounds")),
        };

        return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "sounds-metadata.json")))
               ?? Path.Combine(cwd, "scripts", "meme-sounds");
    }

    // Derives a stable UUID-v5 from the sound slug — same slug always produces the same GUID.
    private static Guid DeriveStableGuid(string slug)
    {
        // UUID v5 with DNS namespace (RFC 4122)
        var namespaceBytes = new byte[] { 0x6b, 0xa7, 0xb8, 0x10, 0x9d, 0xad, 0x11, 0xd1, 0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8 };
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(slug);
        var combined = namespaceBytes.Concat(nameBytes).ToArray();
        // SHA-1 is what a version 5 UUID is defined on. It names a sound here; it protects nothing.
#pragma warning disable CA5350
        var hash = System.Security.Cryptography.SHA1.HashData(combined);
#pragma warning restore CA5350
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50); // version 5
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80); // variant RFC 4122
        return new Guid(hash[..16]);
    }

    private static string? GetArg(string[] args, string flag)
    {
        var idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public sealed record SeedResult(int Seeded, int Skipped, int Failed, int Removed);

    // ── JSON deserialization models ───────────────────────────────────────────
    private sealed class SoundsMetadata
    {
        public string Version { get; set; } = "1.0";
        public List<SoundEntry> Sounds { get; set; } = [];
    }

    private sealed class SoundEntry
    {
        public string? Id { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string Filename { get; set; } = string.Empty;
        public string? SourceUrl { get; set; }
        public int DurationMs { get; set; }
        [JsonPropertyName("actionVectorTags")]
        public string[] ActionVectorTags { get; set; } = [];
        public bool Priority { get; set; }
        public string UseCase { get; set; } = string.Empty;
    }
}
