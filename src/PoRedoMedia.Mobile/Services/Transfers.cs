using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using PoRedoMedia.Shared;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Mobile.Services;

public enum TransferState { Queued, Uploading, Confirming, Starting, Running, Done, Failed }

/// <summary>One "remix this file" job, kept on the phone from the moment it is picked until its render is done.</summary>
public sealed class TransferJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LocalPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }

    /// <summary>A Library item to run on instead of uploading a file.</summary>
    public Guid? SourceId { get; set; }
    public MediaFunction[] Functions { get; set; } = [];
    public Dictionary<string, string> Options { get; set; } = [];
    public string? UploadUrl { get; set; }
    public DateTimeOffset? UploadExpires { get; set; }
    public int BlocksDone { get; set; }
    public TransferState State { get; set; }
    public Guid? RunId { get; set; }
    public int Percent { get; set; }
    public string Message { get; set; } = "Waiting";
    public Guid? FirstOutput { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Items 5 and 10. Uploads go straight to blob storage in 4 MB blocks, one at a time, and each finished block is
/// saved — so no signal, the app in the background, or a restart resumes the upload instead of starting over. Then
/// the upload is confirmed and the run started, and the run is followed over the run hub (with a polling fallback),
/// its step and percent kept on an ongoing notification until the result is ready.
/// </summary>
public sealed class Transfers(ApiClient api, SecureSessionStore tokens, CookieContainer cookies, IBackgroundWork background) : IAsyncDisposable
{
    private const int BlockBytes = 4 * 1024 * 1024;
    private static readonly string StorePath = Path.Combine(FileSystem.AppDataDirectory, "transfers.json");
    private static readonly HttpClient Blob = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly SemaphoreSlim _save = new(1, 1);
    private List<TransferJob> _jobs = Load();
    private HubConnection? _hub;

    public event Action? Changed;

    public IReadOnlyList<TransferJob> Jobs => _jobs;

    public async Task<TransferJob> AddAsync(TransferJob job)
    {
        _jobs.Add(job);
        await SaveAsync();
        _ = PumpAsync();
        return job;
    }

    public async Task ClearFinishedAsync()
    {
        foreach (TransferJob done in _jobs.Where(j => j.State is TransferState.Done or TransferState.Failed && j.SourceId is null && j.LocalPath.StartsWith(FileSystem.CacheDirectory, StringComparison.Ordinal)))
        {
            File.Delete(done.LocalPath);
        }

        _jobs.RemoveAll(j => j.State is TransferState.Done or TransferState.Failed);
        await SaveAsync();
    }

    /// <summary>Moves every unfinished job as far as it can go; safe to call any time (concurrent calls join).</summary>
    public async Task PumpAsync()
    {
        if (!await _pass.WaitAsync(0))
        {
            return;
        }

        try
        {
            foreach (TransferJob job in _jobs.Where(j => j.State is not (TransferState.Done or TransferState.Failed)).ToList())
            {
                if (!ApiClient.Online)
                {
                    job.Message = "Waiting for a connection";
                    continue;
                }

                try
                {
                    await StepAsync(job);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    job.Message = "Connection lost — will resume";
                }

                await SaveAsync();
            }

            await WatchRunsAsync();
        }
        finally
        {
            _pass.Release();
            Report();
        }
    }

    private async Task StepAsync(TransferJob job)
    {
        if (job.State is TransferState.Queued or TransferState.Uploading && job.SourceId is null)
        {
            await UploadAsync(job);
        }

        if (job.State == TransferState.Confirming)
        {
            using HttpResponseMessage confirmed = await api.ConfirmAsync(job.SourceId!.Value);
            if (!confirmed.IsSuccessStatusCode)
            {
                Fail(job, await ApiClient.ProblemAsync(confirmed, "The upload was refused."));
                return;
            }

            job.State = TransferState.Starting;
        }

        if (job.State is TransferState.Starting or TransferState.Queued && job.SourceId is { } source)
        {
            job.Message = "Starting";
            using HttpResponseMessage started = await api.StartRunAsync(new RunRequest(source, job.Functions, job.Options));
            if (!started.IsSuccessStatusCode)
            {
                Fail(job, started.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => "Today's runs are used up.",
                    HttpStatusCode.Conflict => "That file is already being remixed.",
                    _ => await ApiClient.ProblemAsync(started, "The run didn't start."),
                });
                return;
            }

            RunDto run = (await started.Content.ReadFromJsonAsync(WireJson.Default.RunDto))!;
            job.RunId = run.Id;
            job.State = TransferState.Running;
            job.Message = "Queued on the server";
        }
    }

    private async Task UploadAsync(TransferJob job)
    {
        if (!File.Exists(job.LocalPath))
        {
            Fail(job, "The file is gone from the phone.");
            return;
        }

        // A link lasts 15 minutes: an upload that outlives it starts again on a fresh one (the server sweeps the old).
        if (job.UploadUrl is null || job.UploadExpires < DateTimeOffset.UtcNow.AddMinutes(1))
        {
            using HttpResponseMessage ticketResp = await api.StartUploadAsync(job.FileName, job.Size);
            if (!ticketResp.IsSuccessStatusCode)
            {
                Fail(job, await ApiClient.ProblemAsync(ticketResp, "The upload wasn't accepted."));
                return;
            }

            UploadTicket ticket = (await ticketResp.Content.ReadFromJsonAsync(WireJson.Default.UploadTicket))!;
            job.SourceId = ticket.Id;
            job.UploadUrl = ticket.UploadUrl;
            job.UploadExpires = ticket.ExpiresAt;
            job.BlocksDone = 0;
        }

        job.State = TransferState.Uploading;
        int blocks = (int)((job.Size + BlockBytes - 1) / BlockBytes);
        await using FileStream file = File.OpenRead(job.LocalPath);
        byte[] buffer = new byte[BlockBytes];
        for (int i = job.BlocksDone; i < blocks; i++)
        {
            file.Position = (long)i * BlockBytes;
            int read = await file.ReadAtLeastAsync(buffer, (int)Math.Min(BlockBytes, job.Size - file.Position));
            using ByteArrayContent body = new(buffer, 0, read);
            using HttpResponseMessage put = await Blob.PutAsync($"{job.UploadUrl}&comp=block&blockid={Uri.EscapeDataString(BlockId(i))}", body);
            if (put.StatusCode == HttpStatusCode.Forbidden)
            {
                job.UploadUrl = null; // the link lapsed mid-upload
                return;
            }

            put.EnsureSuccessStatusCode();
            job.BlocksDone = i + 1;
            job.Percent = (int)(100.0 * job.BlocksDone / blocks);
            job.Message = $"Uploading {job.Percent}%";
            await SaveAsync();
            Report();
        }

        StringBuilder list = new("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>");
        for (int i = 0; i < blocks; i++)
        {
            list.Append("<Latest>").Append(BlockId(i)).Append("</Latest>");
        }

        using StringContent xml = new(list.Append("</BlockList>").ToString(), Encoding.UTF8, "application/xml");
        using HttpResponseMessage commit = await Blob.PutAsync($"{job.UploadUrl}&comp=blocklist", xml);
        commit.EnsureSuccessStatusCode();
        job.State = TransferState.Confirming;
        job.Message = "Checking the upload";
    }

    private static string BlockId(int index) => Convert.ToBase64String(Encoding.ASCII.GetBytes($"blk-{index:D6}"));

    private static void Fail(TransferJob job, string why)
    {
        job.State = TransferState.Failed;
        job.Message = why;
    }

    // ── Render progress (item 10) ────────────────────────────────────────────

    private async Task WatchRunsAsync()
    {
        List<TransferJob> running = [.. _jobs.Where(j => j.State == TransferState.Running && j.RunId is not null)];
        if (running.Count == 0)
        {
            return;
        }

        try
        {
            if (_hub is null)
            {
                _hub = new HubConnectionBuilder()
                    .WithUrl(api.U("hubs/run"), o =>
                    {
                        o.AccessTokenProvider = tokens.GetBearerTokenAsync;
                        o.Cookies = cookies;
                    })
                    .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, WireJson.Default))
                    .WithAutomaticReconnect()
                    .Build();
                _hub.On<RunProgressDto>("RunProgress", OnProgress);
                await _hub.StartAsync();
            }

            foreach (TransferJob job in running)
            {
                await _hub.InvokeAsync("JoinRun", job.RunId!.Value.ToString());
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            // No hub (proxy, sleeping phone): poll instead.
        }

        foreach (TransferJob job in running)
        {
            if (await api.GetRunAsync(job.RunId!.Value) is { } run)
            {
                Apply(job, run.Status, run.CurrentStep, job.Percent, run.Error ?? job.Message, run.OutputIds.FirstOrDefault());
            }
        }
    }

    private void OnProgress(RunProgressDto p)
    {
        if (_jobs.FirstOrDefault(j => j.RunId == p.RunId) is { } job)
        {
            Apply(job, p.Status, p.Step, p.Percent, p.Message, p.Output?.Id ?? Guid.Empty);
            _ = SaveAsync();
            Report();
        }
    }

    private void Apply(TransferJob job, RunStatus status, MediaFunction? step, int percent, string message, Guid output)
    {
        job.Percent = percent;
        job.Message = step is { } s ? $"{FunctionStack.Label(s)}: {message}" : message;
        if (output != Guid.Empty)
        {
            job.FirstOutput ??= output;
        }

        if (status is RunStatus.Complete or RunStatus.Failed)
        {
            job.State = status == RunStatus.Complete ? TransferState.Done : TransferState.Failed;
            background.Notify(
                status == RunStatus.Complete ? "Your remix is ready" : "The remix failed",
                status == RunStatus.Complete ? string.Join(" + ", job.Functions.Select(FunctionStack.Label)) : message,
                job.FirstOutput is { } id ? $"{MobileConfig.AppScheme}://media/{id}" : null);
        }
    }

    /// <summary>The ongoing notification follows whatever is still moving; it goes away when nothing is.</summary>
    private void Report()
    {
        List<TransferJob> active = [.. _jobs.Where(j => j.State is not (TransferState.Done or TransferState.Failed))];
        if (active.Count == 0)
        {
            background.End();
        }
        else
        {
            TransferJob lead = active[0];
            background.Progress(
                active.Count == 1 ? string.Join(" + ", lead.Functions.Select(FunctionStack.Label)) : $"{active.Count} remixes in progress",
                lead.Message,
                lead.Percent);
        }

        Changed?.Invoke();
    }

    private async Task SaveAsync()
    {
        await _save.WaitAsync();
        try
        {
            string temp = StorePath + ".tmp";
            await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(_jobs, MobileJson.Default.ListTransferJob));
            File.Move(temp, StorePath, overwrite: true);
        }
        finally
        {
            _save.Release();
        }
    }

    private static List<TransferJob> Load()
    {
        try
        {
            return File.Exists(StorePath) ? JsonSerializer.Deserialize(File.ReadAllBytes(StorePath), MobileJson.Default.ListTransferJob) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
        }

        _pass.Dispose();
        _save.Dispose();
    }
}
