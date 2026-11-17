using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using PoRedoMedia.Movie.Render;

namespace PoRedoMedia.Api.Features.Publishing;

/// <summary>
/// YouTube Data API v3 over plain HTTP, as Google documents it (checked 2026-11-17):
/// OAuth web-server flow (accounts.google.com/o/oauth2/v2/auth with access_type=offline and prompt=consent, so a
/// refresh token comes back; oauth2.googleapis.com/token and /revoke), scope youtube.upload (enough for videos.insert
/// and thumbnails.set), and the resumable upload protocol: a session opened with X-Upload-Content-Length, then chunks
/// that are a multiple of 256 KiB with Content-Range, answered 308 (with a Range of what arrived) until the last, 201.
/// </summary>
public sealed class GoogleYouTubePublisher(string? clientId, string? clientSecret, Func<HttpClient> http) : IYouTubePublisher
{
    public const string Scope = "https://www.googleapis.com/auth/youtube.upload";

    /// <summary>Each chunk sent: 32 × 256 KiB = 8 MiB.</summary>
    internal const int ChunkSize = 32 * 256 * 1024;

    public bool Configured => !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret);

    public Uri SignInPage(string redirect, string state) => new("https://accounts.google.com/o/oauth2/v2/auth?" + string.Join('&',
        $"client_id={Uri.EscapeDataString(clientId ?? "")}", $"redirect_uri={Uri.EscapeDataString(redirect)}", "response_type=code",
        $"scope={Uri.EscapeDataString(Scope)}", "access_type=offline", "prompt=consent", "include_granted_scopes=true", $"state={Uri.EscapeDataString(state)}"));

    public async Task<PlatformTokens> ExchangeAsync(string code, string redirect, CancellationToken ct)
    {
        var json = await TokenAsync(new()
        {
            ["code"] = code,
            ["client_id"] = clientId!,
            ["client_secret"] = clientSecret!,
            ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code",
        }, ct);
        return new PlatformTokens(json["access_token"]?.GetValue<string>() ?? throw new PublishException(PublishFailure.Unavailable, "Google gave no pass to upload with."),
            json["refresh_token"]?.GetValue<string>());
    }

    public async Task<string> RefreshAsync(string refreshToken, CancellationToken ct) =>
        (await TokenAsync(new()
        {
            ["client_id"] = clientId!,
            ["client_secret"] = clientSecret!,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, ct))["access_token"]?.GetValue<string>() ?? throw new PublishException(PublishFailure.Reconnect, "Google gave no pass to upload with. Connect YouTube again.");

    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        using var reply = await http().PostAsync("https://oauth2.googleapis.com/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), ct);
        // A token Google already forgot answers 400: it is revoked either way.
    }

    public async Task<YouTubeUploaded> UploadAsync(string access, string videoPath, YouTubeVideo video, IProgress<double>? progress, CancellationToken ct)
    {
        var length = new FileInfo(videoPath).Length;
        using var start = StartRequest(access, video, length);
        using var started = await http().SendAsync(start, ct);
        await JsonAsync(started, ct);
        var session = started.Headers.Location ?? throw new PublishException(PublishFailure.Unavailable, "YouTube did not say where to send the video.");

        await using var file = File.OpenRead(videoPath);
        var buffer = new byte[ChunkSize];
        long sent = 0;
        while (true)
        {
            file.Position = sent;
            var count = await file.ReadAtLeastAsync(buffer, (int)Math.Min(ChunkSize, length - sent), throwOnEndOfStream: false, ct);
            using var put = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent(buffer, 0, count) };
            put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            put.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            put.Content.Headers.ContentRange = new ContentRangeHeaderValue(sent, sent + count - 1, length);
            using var reply = await http().SendAsync(put, ct);
            if ((int)reply.StatusCode == 308)
            {
                // What arrived so far; carry on from just after it (nothing arrived when there is no Range).
                sent = Arrived(reply);
                progress?.Report((double)sent / length);
                continue;
            }
            var json = await JsonAsync(reply, ct);
            progress?.Report(1);
            return new YouTubeUploaded(json["id"]?.GetValue<string>() ?? throw new PublishException(PublishFailure.Unavailable, "YouTube gave no id for the new video."),
                json["status"]?["privacyStatus"]?.GetValue<string>() ?? video.Privacy);
        }
    }

    public async Task SetThumbnailAsync(string access, string videoId, string jpegPath, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://www.googleapis.com/upload/youtube/v3/thumbnails/set?videoId={Uri.EscapeDataString(videoId)}")
        {
            Content = new ByteArrayContent(await File.ReadAllBytesAsync(jpegPath, ct)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var reply = await http().SendAsync(request, ct);
        await JsonAsync(reply, ct);
    }

    /// <summary>The request that opens a resumable upload: the details, held to YouTube's limits, and how much is coming.</summary>
    internal static HttpRequestMessage StartRequest(string access, YouTubeVideo video, long length)
    {
        var body = new JsonObject
        {
            ["snippet"] = new JsonObject
            {
                ["title"] = PublishPackage.YouTubeSafe(video.Title, PublishPackage.YouTubeTitleMax),
                ["description"] = Bytes(PublishPackage.YouTubeSafe(video.Description, PublishPackage.YouTubeDescriptionMax), PublishPackage.YouTubeDescriptionMax),
                ["tags"] = new JsonArray([.. PublishPackage.FitTags(video.Tags).Select(t => (JsonNode)JsonValue.Create(t))]),
                ["categoryId"] = video.CategoryId,
            },
            ["status"] = new JsonObject
            {
                ["privacyStatus"] = video.Privacy,
                ["selfDeclaredMadeForKids"] = video.MadeForKids,
            },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.Add("X-Upload-Content-Type", "video/mp4");
        request.Headers.Add("X-Upload-Content-Length", length.ToString(CultureInfo.InvariantCulture));
        return request;
    }

    /// <summary>YouTube counts a description's limit in UTF-8 bytes: cut to fit at a character boundary.</summary>
    internal static string Bytes(string text, int most)
    {
        if (Encoding.UTF8.GetByteCount(text) <= most) return text;
        var end = text.Length;
        while (end > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, end)) > most - 3) end--;
        if (end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end] + "…";
    }

    /// <summary>The byte after the last one YouTube says arrived (a 308's "Range: bytes=0-N"); 0 when none did.</summary>
    internal static long Arrived(HttpResponseMessage reply) =>
        reply.Headers.TryGetValues("Range", out var ranges) && ranges.FirstOrDefault() is { } range
            && long.TryParse(range[(range.LastIndexOf('-') + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var last)
            ? last + 1
            : 0;

    private async Task<JsonObject> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var reply = await http().PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        return await JsonAsync(reply, ct);
    }

    /// <summary>The reply's JSON; a refusal sorted into what the user can do about it.</summary>
    internal static async Task<JsonObject> JsonAsync(HttpResponseMessage reply, CancellationToken ct)
    {
        var text = await reply.Content.ReadAsStringAsync(ct);
        JsonObject? json = null;
        try
        {
            json = text.Length > 0 ? JsonNode.Parse(text) as JsonObject : null;
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON: judged by its status below.
        }
        if (reply.IsSuccessStatusCode)
            return json ?? [];
        throw Failure(reply.StatusCode, json);
    }

    /// <summary>Google's refusal as the user should hear it (errors as documented for the Data API and OAuth).</summary>
    internal static PublishException Failure(HttpStatusCode status, JsonObject? json)
    {
        // The Data API answers {"error":{"errors":[{"reason":…}],"message":…}}; OAuth answers {"error":"invalid_grant",…}.
        var api = json?["error"] as JsonObject;
        var reason = api?["errors"]?[0]?["reason"]?.GetValue<string>() ?? (json?["error"] as JsonValue)?.GetValue<string>();
        var said = api?["message"]?.GetValue<string>() ?? json?["error_description"]?.GetValue<string>();
        return reason switch
        {
            "invalid_grant" or "unauthorized_client" or "authError" or "invalid_token" => new(PublishFailure.Reconnect, "YouTube no longer accepts this connection (it expired or was removed). Connect YouTube again."),
            "quotaExceeded" or "rateLimitExceeded" or "userRateLimitExceeded" => new(PublishFailure.Quota, "YouTube's daily allowance for this app is used up. Try again tomorrow."),
            "uploadLimitExceeded" => new(PublishFailure.Quota, "This YouTube channel has reached its upload limit for today. Try again tomorrow."),
            _ when status == HttpStatusCode.Unauthorized => new(PublishFailure.Reconnect, "YouTube no longer accepts this connection. Connect YouTube again."),
            _ when (int)status >= 500 || status == HttpStatusCode.NotFound => new(PublishFailure.Unavailable, $"YouTube could not take it just now ({(int)status}). Try again."),
            _ => new(PublishFailure.Refused, $"YouTube said no ({(int)status}): {said ?? reason ?? "no reason given"}"),
        };
    }
}
