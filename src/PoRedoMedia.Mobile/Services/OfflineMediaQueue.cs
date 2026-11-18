using System.Text.Json;
using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.Mobile.Services;

public sealed record QueuedMediaUpload(string Id, string FileName, string LocalPath, MediaFunction Function, DateTimeOffset CreatedUtc);

public sealed class OfflineMediaQueue : IDisposable
{
    private readonly string _queueDir = Path.Combine(FileSystem.AppDataDirectory, "poredomedia-queue");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OfflineMediaQueue()
    {
        Directory.CreateDirectory(_queueDir);
    }

    public void Dispose() => _gate.Dispose();

    public async Task<QueuedMediaUpload> EnqueueAsync(string sourcePath, string fileName, MediaFunction fn, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var target = Path.Combine(_queueDir, $"{id}_{Path.GetFileName(fileName)}");
            File.Copy(sourcePath, target, overwrite: true);
            var item = new QueuedMediaUpload(id, fileName, target, fn, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(Path.Combine(_queueDir, $"{id}.json"), JsonSerializer.Serialize(item), ct).ConfigureAwait(false);
            return item;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<QueuedMediaUpload>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var items = new List<QueuedMediaUpload>();
            foreach (var file in Directory.EnumerateFiles(_queueDir, "*.json"))
            {
                var item = JsonSerializer.Deserialize<QueuedMediaUpload>(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            return [.. items.OrderBy(i => i.CreatedUtc)];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var meta = Path.Combine(_queueDir, $"{id}.json");
            if (File.Exists(meta))
            {
                var item = JsonSerializer.Deserialize<QueuedMediaUpload>(await File.ReadAllTextAsync(meta, ct).ConfigureAwait(false));
                if (item is not null && File.Exists(item.LocalPath))
                {
                    File.Delete(item.LocalPath);
                }

                File.Delete(meta);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
