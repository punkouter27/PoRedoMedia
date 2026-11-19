using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Mobile.Services;

public sealed record AntiforgeryToken(string Token);

/// <summary>JSON for what the phone keeps itself (the wire DTOs use Shared's <see cref="WireJson"/>).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AntiforgeryToken))]
[JsonSerializable(typeof(List<TransferJob>))]
internal sealed partial class MobileJson : JsonSerializerContext;

/// <summary>The phone's calls, on the same routes and wire types as the web client.</summary>
public sealed class ApiClient(HttpClient http, MobileConfig config, SecureSessionStore tokens, CookieContainer cookies)
{
    private static readonly WireJson Wire = WireJson.Default;

    // Content and thumbnails answer with a redirect to a short-lived blob link; that link is what the system
    // downloader and the video player are given, so no credential ever leaves the app.
    private static readonly HttpClient NoRedirect = new(new HttpClientHandler { AllowAutoRedirect = false });

    public static bool Online => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    public Uri U(string path) => new(new Uri(config.BaseUrl + "/"), path.TrimStart('/'));

    public HttpClient Http => http;

    public Task<AppConfigDto?> GetConfigAsync() => ReadAsync("api/config", Wire.AppConfigDto);

    /// <summary>Signed in = the quota answers (it needs a user).</summary>
    public Task<QuotaStatusDto?> GetQuotaAsync() => ReadAsync("api/quota", Wire.QuotaStatusDto);

    public Task<StorageUsageDto?> GetStorageAsync() => ReadAsync("api/media/storage", Wire.StorageUsageDto);

    public Task<List<MediaDto>?> ListMediaAsync() => ReadAsync("api/media", Wire.ListMediaDto);

    public Task<MediaDto?> GetMediaAsync(Guid id) => ReadAsync($"api/media/{id}", Wire.MediaDto);

    public Task<List<RunDto>?> ListRunsAsync() => ReadAsync("api/runs", Wire.ListRunDto);

    public Task<RunDto?> GetRunAsync(Guid id) => ReadAsync($"api/runs/{id}", Wire.RunDto);

    public Task<List<MemeTemplateDto>?> MemeTemplatesAsync() => ReadAsync("api/meme-templates", Wire.ListMemeTemplateDto);

    public Task<bool> DevLoginAsync(string email) => SendAsync(() => http.GetAsync(U($"dev-login?email={Uri.EscapeDataString(email)}")));

    public Task<bool> LogoutAsync() => SendAsync(() => http.PostAsync(U("logout"), null));

    public Task<bool> UpdateMediaAsync(Guid id, string? title, bool? pinned) => SendAsync(() => http.PutAsJsonAsync(U($"api/media/{id}"), new MediaUpdateRequest(title, pinned), Wire.MediaUpdateRequest));

    public Task<bool> DeleteMediaAsync(Guid id) => SendAsync(() => http.DeleteAsync(U($"api/media/{id}")));

    public Task<bool> CancelRunAsync(Guid id) => SendAsync(() => http.DeleteAsync(U($"api/runs/{id}")));

    public async Task<string?> ShareAsync(Guid id)
    {
        try
        {
            using HttpResponseMessage resp = await http.PostAsync(U($"api/media/{id}/share"), null);
            return resp.IsSuccessStatusCode ? (await resp.Content.ReadFromJsonAsync(Wire.ShareLinkDto))?.Url : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public Task<HttpResponseMessage> StartUploadAsync(string fileName, long size) => http.PostAsJsonAsync(U("api/media/sas"), new UploadRequest(fileName, size), Wire.UploadRequest);

    public Task<HttpResponseMessage> ConfirmAsync(Guid id) => http.PostAsync(U($"api/media/{id}/confirm"), null);

    public Task<HttpResponseMessage> StartRunAsync(RunRequest request) => http.PostAsJsonAsync(U("api/runs"), request, Wire.RunRequest);

    /// <summary>The blob link behind /content (or /thumb): resolved with the app's credentials, followed by nobody else.</summary>
    public async Task<Uri?> BlobLinkAsync(Guid id, bool thumb = false)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, U($"api/media/{id}/{(thumb ? "thumb" : "content")}"));
        // Same credentials as the main client, applied by hand because this one must not follow the redirect.
        if (await tokens.GetBearerTokenAsync() is { Length: > 0 } bearer)
        {
            request.Headers.Authorization = new("Bearer", bearer);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookies.GetCookieHeader(request.RequestUri!));
        }

        try
        {
            using HttpResponseMessage resp = await NoRedirect.SendAsync(request);
            return resp.Headers.Location is { } location ? (location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location)) : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public static async Task<string> ProblemAsync(HttpResponseMessage resp, string fallback)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("detail", out JsonElement d) && d.GetString() is { Length: > 0 } detail ? detail
                : doc.RootElement.TryGetProperty("title", out JsonElement t) && t.GetString() is { Length: > 0 } title ? title : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private async Task<T?> ReadAsync<T>(string path, JsonTypeInfo<T> type)
    {
        try
        {
            using HttpResponseMessage resp = await http.GetAsync(U(path));
            return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync(type) : default;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return default;
        }
    }

    private static async Task<bool> SendAsync(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            using HttpResponseMessage resp = await send();
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
