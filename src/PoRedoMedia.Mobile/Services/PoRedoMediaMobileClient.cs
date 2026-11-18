using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Mobile.Services;

public sealed class PoRedoMediaMobileClient(
    HttpClient http,
    MobileConfig config,
    SecureSessionStore sessionStore) : IAsyncDisposable
{
    private HubConnection? _hub;

    public event Action<RunProgressDto>? RunProgressed;

    private Uri BuildUri(string relativePath) => new(new Uri(config.BaseUrl + "/"), relativePath.TrimStart('/'));

    public async Task<QuotaStatusDto?> GetQuotaAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<QuotaStatusDto>(BuildUri("/api/quota"), ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<RunDto>> GetRunsAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<List<RunDto>>(BuildUri("/api/runs"), ct).ConfigureAwait(false) ?? [];

    public async Task<RunDto> UploadAndRunAsync(string localPath, string fileName, MediaFunction function, CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(localPath);
        using var ticketResp = await http.PostAsJsonAsync(
            BuildUri("/api/media/sas"),
            new UploadRequest(fileName, fileInfo.Length),
            ct).ConfigureAwait(false);
        ticketResp.EnsureSuccessStatusCode();

        var ticket = await ticketResp.Content.ReadFromJsonAsync<UploadTicket>(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Missing UploadTicket.");

        var uploadUrl = RewriteLocalAzuriteHost(ticket.UploadUrl);
        await using var stream = File.OpenRead(localPath);
        using var blobHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        using var putReq = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
        {
            Content = new StreamContent(stream),
        };
        putReq.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        using var putResp = await blobHttp.SendAsync(putReq, ct).ConfigureAwait(false);
        putResp.EnsureSuccessStatusCode();

        using var confirmResp = await http.PostAsync(BuildUri($"/api/media/{ticket.Id}/confirm"), content: null, ct).ConfigureAwait(false);
        confirmResp.EnsureSuccessStatusCode();

        using var runResp = await http.PostAsJsonAsync(
            BuildUri("/api/runs"),
            new RunRequest(ticket.Id, [function]),
            ct).ConfigureAwait(false);
        runResp.EnsureSuccessStatusCode();

        return await runResp.Content.ReadFromJsonAsync<RunDto>(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Missing RunDto.");
    }

    public async Task WatchRunAsync(Guid runId, CancellationToken ct = default)
    {
        var fakeUser = await sessionStore.GetFakeUserAsync().ConfigureAwait(false);
        if (_hub is null)
        {
            _hub = new HubConnectionBuilder()
                .WithUrl(BuildUri("/hubs/run"), options =>
                {
                    options.AccessTokenProvider = () => sessionStore.GetBearerTokenAsync();
                    if (!string.IsNullOrWhiteSpace(fakeUser))
                    {
                        options.Headers["X-Fake-User"] = fakeUser;
                    }
                })
                .WithAutomaticReconnect()
                .Build();
            _hub.On<RunProgressDto>("RunProgress", progress => RunProgressed?.Invoke(progress));
        }

        if (_hub.State == HubConnectionState.Disconnected)
        {
            await _hub.StartAsync(ct).ConfigureAwait(false);
        }

        await _hub.InvokeAsync("JoinRun", runId.ToString(), ct).ConfigureAwait(false);
    }

    private static string RewriteLocalAzuriteHost(string sasUrl)
    {
        if (DeviceInfo.Current.Platform != DevicePlatform.Android)
        {
            return sasUrl;
        }

        return sasUrl
            .Replace("http://127.0.0.1", "http://10.0.2.2", StringComparison.OrdinalIgnoreCase)
            .Replace("http://localhost", "http://10.0.2.2", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync().ConfigureAwait(false);
        }
    }
}
