using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace PoRedoMedia.Api.Features.Publishing;

/// <summary>
/// TikTok over plain HTTP, as its developer docs describe it (checked 2026-11-17): Login Kit for Web
/// (www.tiktok.com/v2/auth/authorize/ with comma-separated scopes; open.tiktokapis.com/v2/oauth/token/ and /revoke/;
/// refresh tokens rotate), and the Content Posting API with FILE_UPLOAD: an init call (inbox/video/init for a draft,
/// video/init for a direct post) gives an upload URL, the file goes in order in chunks of 5 to 64 MB with Content-Range
/// (206 until the last, 201), and status/fetch says how it went. Nothing is pulled from a URL, so no domain needs
/// verifying.
/// </summary>
public sealed class TikTokPublisher(string? clientKey, string? clientSecret, bool directPost, Func<HttpClient> http) : ITikTokPublisher
{
    private const string Api = "https://open.tiktokapis.com/v2/";

    public bool Configured => !string.IsNullOrWhiteSpace(clientKey) && !string.IsNullOrWhiteSpace(clientSecret);

    public bool DirectPost => Configured && directPost;

    /// <summary>video.upload sends drafts to the inbox; video.publish posts directly, asked for only when set up.</summary>
    public string Scopes => DirectPost ? "user.info.basic,video.upload,video.publish" : "user.info.basic,video.upload";

    public Uri SignInPage(string redirect, string state) => new("https://www.tiktok.com/v2/auth/authorize/?" + string.Join('&',
        $"client_key={Uri.EscapeDataString(clientKey ?? "")}", $"scope={Uri.EscapeDataString(Scopes)}", "response_type=code",
        $"redirect_uri={Uri.EscapeDataString(redirect)}", $"state={Uri.EscapeDataString(state)}"));

    public async Task<PlatformTokens> ExchangeAsync(string code, string redirect, CancellationToken ct) => Tokens(await FormAsync("oauth/token/", new()
    {
        ["client_key"] = clientKey!,
        ["client_secret"] = clientSecret!,
        ["code"] = code,
        ["grant_type"] = "authorization_code",
        ["redirect_uri"] = redirect,
    }, ct));

    public async Task<PlatformTokens> RefreshAsync(string refreshToken, CancellationToken ct) => Tokens(await FormAsync("oauth/token/", new()
    {
        ["client_key"] = clientKey!,
        ["client_secret"] = clientSecret!,
        ["grant_type"] = "refresh_token",
        ["refresh_token"] = refreshToken,
    }, ct));

    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        try
        {
            await FormAsync("oauth/revoke/", new() { ["client_key"] = clientKey!, ["client_secret"] = clientSecret!, ["token"] = token }, ct);
        }
        catch (PublishException)
        {
            // A token TikTok already forgot is revoked either way.
        }
    }

    public async Task<IReadOnlyList<string>> PrivacyOptionsAsync(string access, CancellationToken ct)
    {
        var json = await JsonAsync(access, "post/publish/creator_info/query/", new JsonObject(), ct);
        return json["data"]?["privacy_level_options"] is JsonArray options ? [.. options.Select(o => o!.GetValue<string>())] : ["SELF_ONLY"];
    }

    public async Task<TikTokSent> SendAsync(string access, string videoPath, string? caption, string? privacy, IProgress<double>? progress, CancellationToken ct)
    {
        var size = new FileInfo(videoPath).Length;
        var (chunk, count) = ChunkPlan(size);
        var source = new JsonObject { ["source"] = "FILE_UPLOAD", ["video_size"] = size, ["chunk_size"] = chunk, ["total_chunk_count"] = count };
        var init = caption is null
            ? await JsonAsync(access, "post/publish/inbox/video/init/", new JsonObject { ["source_info"] = source }, ct)
            : await JsonAsync(access, "post/publish/video/init/", new JsonObject
            {
                ["post_info"] = new JsonObject { ["title"] = caption, ["privacy_level"] = privacy ?? "SELF_ONLY" },
                ["source_info"] = source,
            }, ct);
        var publishId = init["data"]?["publish_id"]?.GetValue<string>() ?? throw new PublishException(PublishFailure.Unavailable, "TikTok did not take the upload.");
        var url = init["data"]?["upload_url"]?.GetValue<string>() ?? throw new PublishException(PublishFailure.Unavailable, "TikTok did not say where to send the video.");

        await using var file = File.OpenRead(videoPath);
        for (var i = 0; i < count; i++)
        {
            var first = (long)i * chunk;
            var length = (int)(i == count - 1 ? size - first : chunk);
            var buffer = new byte[length];
            file.Position = first;
            await file.ReadExactlyAsync(buffer, ct);
            using var put = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(buffer) };
            put.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            put.Content.Headers.ContentRange = new ContentRangeHeaderValue(first, first + length - 1, size);
            using var reply = await http().SendAsync(put, ct);
            if (!reply.IsSuccessStatusCode)
                throw (int)reply.StatusCode == 403
                    ? new PublishException(PublishFailure.Unavailable, "TikTok's upload link ran out before the video was sent. Try again.")
                    : new PublishException(PublishFailure.Unavailable, $"TikTok broke off the upload ({(int)reply.StatusCode}). Try again.");
            progress?.Report((double)(first + length) / size);
        }

        var status = await JsonAsync(access, "post/publish/status/fetch/", new JsonObject { ["publish_id"] = publishId }, ct);
        var state = status["data"]?["status"]?.GetValue<string>() ?? "PROCESSING_UPLOAD";
        if (state == "FAILED")
            throw new PublishException(PublishFailure.Refused, $"TikTok could not take the video ({status["data"]?["fail_reason"]?.GetValue<string>() ?? "no reason given"}).");
        return new TikTokSent(publishId, state);
    }

    /// <summary>
    /// How to cut a file into TikTok's chunks: one chunk when under 20 MB; otherwise 10 MB chunks, the last one taking
    /// what is left over (so it stays under TikTok's 128 MB for a last chunk), in at most 1000 chunks.
    /// </summary>
    internal static (long ChunkSize, int Count) ChunkPlan(long size)
    {
        const long Small = 5_000_000, Chunk = 10_000_000;
        if (size < Small) return (size, 1);
        var count = (int)Math.Max(1, size / Chunk);
        return (count == 1 ? size : Chunk, count); // under 20 MB is one chunk of its own size, within TikTok's 64 MB
    }

    private static PlatformTokens Tokens(JsonObject json) =>
        new(json["access_token"]?.GetValue<string>() ?? throw new PublishException(PublishFailure.Reconnect, "TikTok gave no pass to post with. Connect TikTok again."),
            json["refresh_token"]?.GetValue<string>());

    private async Task<JsonObject> FormAsync(string path, Dictionary<string, string> form, CancellationToken ct)
    {
        using var reply = await http().PostAsync(Api + path, new FormUrlEncodedContent(form), ct);
        return await ReadAsync(reply, ct);
    }

    private async Task<JsonObject> JsonAsync(string access, string path, JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Api + path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var reply = await http().SendAsync(request, ct);
        return await ReadAsync(reply, ct);
    }

    /// <summary>The reply's JSON; TikTok's error (in the body, often with 200) sorted into what the user can do.</summary>
    internal static async Task<JsonObject> ReadAsync(HttpResponseMessage reply, CancellationToken ct)
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
        var code = json?["error"] is JsonObject error ? error["code"]?.GetValue<string>() : json?["error"]?.GetValue<string>();
        if (reply.IsSuccessStatusCode && code is null or "ok")
            return json ?? [];
        throw Failure(code, (int)reply.StatusCode);
    }

    internal static PublishException Failure(string? code, int status) => code switch
    {
        "access_token_invalid" or "invalid_grant" or "scope_not_authorized"
            => new(PublishFailure.Reconnect, "TikTok no longer accepts this connection (it expired or was removed). Connect TikTok again."),
        "unaudited_client_can_only_post_to_private_accounts"
            => new(PublishFailure.Refused, "TikTok has not reviewed this app yet, so it may only post privately (Only me), and only to a private account. Send it as a draft instead, or set the account to private."),
        "spam_risk_too_many_posts" or "spam_risk_user_banned_from_posting" or "reached_active_user_cap" or "rate_limit_exceeded" or "spam_risk_too_many_pending_share"
            => new(PublishFailure.Quota, "TikTok will not take more posts from this account or app today. Try again tomorrow, and post or clear the drafts waiting in your TikTok inbox."),
        _ when status == 401 => new(PublishFailure.Reconnect, "TikTok no longer accepts this connection. Connect TikTok again."),
        _ when status >= 500 => new(PublishFailure.Unavailable, $"TikTok could not take it just now ({status}). Try again."),
        _ => new(PublishFailure.Refused, $"TikTok said no ({code ?? status.ToString(System.Globalization.CultureInfo.InvariantCulture)})."),
    };
}
