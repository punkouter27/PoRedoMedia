using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PoRedoMedia.Movie.Render;

/// <summary>Sends one request over the web. The seam tests use to read what would be sent, with no network.</summary>
public delegate Task<HttpResponseMessage> SendHttp(HttpRequestMessage request, CancellationToken ct);

public enum Privacy { Private, Unlisted, Public }

/// <param name="PublishAt">When the video goes public by itself; it is uploaded private until then. Null for no schedule.</param>
public sealed record UploadDetails(string Title, string Description, IReadOnlyList<string> Tags, Privacy Privacy, DateTimeOffset? PublishAt = null)
{
    /// <summary>
    /// The video is made for children, as every kids' series video is. YouTube and US law (COPPA) ask that this be said
    /// at upload: comments and personalised ads are then off.
    /// </summary>
    public bool MadeForKids { get; init; }

    /// <summary>YouTube's number for the kind of video: 28 is Science and Technology, 22 People and Blogs.</summary>
    public string Category { get; init; } = "28";
}

/// <summary>
/// Uploads to the owner's YouTube channel with the YouTube Data API. Every upload signs in afresh with Google, and
/// the pass Google gives back is held in memory for that one job: nothing is kept on disk, and no lasting pass is asked for.
/// </summary>
/// <remarks>
/// TikTok is not offered: its posting API only works for apps TikTok has reviewed and approved.
/// </remarks>
public sealed class YouTube(string clientId, string clientSecret, SendHttp send)
{
    private const string Scope = "https://www.googleapis.com/auth/youtube.upload";

    /// <summary>The limits YouTube sets on what it is sent.</summary>
    private const int MaxTitle = 100, MaxDescription = 5000, MaxTags = 500;

    /// <summary>A fresh proof for one sign-in (PKCE): the secret half is kept here, the other goes to Google with the user.</summary>
    public static (string Verifier, string Challenge) NewProof()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    /// <summary>A value nobody could guess, to tell this sign-in from any other when Google sends the browser back.</summary>
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// The Google page the user signs in on, as a web app's sign-in goes: Google then sends the browser back to
    /// <paramref name="redirect"/> with a one-time code and the <paramref name="state"/> it was given.
    /// </summary>
    public Uri SignInPage(string redirect, string state, string challenge)
    {
        string[] query =
        [
            $"client_id={Uri.EscapeDataString(clientId)}", $"redirect_uri={Uri.EscapeDataString(redirect)}", "response_type=code",
            $"scope={Uri.EscapeDataString(Scope)}", $"code_challenge={challenge}", "code_challenge_method=S256", $"state={state}",
        ];
        return new Uri("https://accounts.google.com/o/oauth2/v2/auth?" + string.Join('&', query));
    }

    /// <summary>Swaps the one-time code for a pass to upload with. A code caught on the way is no use without the proof's secret half.</summary>
    /// <returns>The access token, good for about an hour. It is held for the one job and never kept.</returns>
    public async Task<string> TokenAsync(string code, string redirect, string verifier, CancellationToken ct)
    {
        using var exchange = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["redirect_uri"] = redirect,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = verifier,
            }),
        };
        using var reply = await send(exchange, ct);
        return (await JsonAsync(reply, ct))["access_token"]?.GetValue<string>() ?? throw new InvalidDataException("Google did not give a pass to upload with.");
    }

    /// <summary>
    /// Sends a video and its details, a little at a time so the progress can be shown, and gives the new video's id.
    /// </summary>
    /// <param name="progress">Told how much has been sent, 0 to 1.</param>
    public async Task<string> UploadAsync(string token, string video, UploadDetails details, IProgress<double>? progress, CancellationToken ct)
    {
        using var start = StartRequest(token, details, new FileInfo(video).Length);
        using var started = await send(start, ct);
        await JsonAsync(started, ct);
        var location = started.Headers.Location ?? throw new InvalidDataException("YouTube did not say where to send the video.");
        using var put = new HttpRequestMessage(HttpMethod.Put, location) { Content = new FileContent(video, progress) };
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        using var done = await send(put, ct);
        return (await JsonAsync(done, ct))["id"]?.GetValue<string>() ?? throw new InvalidDataException("YouTube did not give the new video's id.");
    }

    /// <summary>
    /// The request that opens an upload: the video's details, held to YouTube's limits, and how much is coming. A
    /// scheduled video is sent private, as YouTube asks, and goes public at its time.
    /// </summary>
    public static HttpRequestMessage StartRequest(string token, UploadDetails details, long length)
    {
        var tags = new JsonArray();
        var room = MaxTags;
        foreach (var tag in details.Tags.Select(Clean).Where(t => t.Length > 0))
            if ((room -= tag.Length + 1) >= 0) tags.Add((JsonNode)JsonValue.Create(tag));
        var status = new JsonObject
        {
            ["privacyStatus"] = (details.PublishAt is null ? details.Privacy : Privacy.Private).ToString().ToLowerInvariant(),
            ["selfDeclaredMadeForKids"] = details.MadeForKids,
        };
        if (details.PublishAt is { } at)
            status["publishAt"] = at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var body = new JsonObject
        {
            ["snippet"] = new JsonObject
            {
                ["title"] = Cut(Clean(details.Title), MaxTitle),
                ["description"] = Cut(Clean(details.Description), MaxDescription),
                ["tags"] = tags,
                ["categoryId"] = details.Category,
            },
            ["status"] = status,
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Upload-Content-Type", "video/mp4");
        request.Headers.Add("X-Upload-Content-Length", length.ToString(CultureInfo.InvariantCulture));
        return request;

        // YouTube turns away a title or description with angle brackets in it.
        static string Clean(string text) => text.Replace("<", "").Replace(">", "").Trim();
        static string Cut(string text, int most) => text.Length > most ? text[..most].TrimEnd() : text;
    }

    /// <summary>Sets a video's thumbnail: a JPEG under 2 MB. YouTube allows it only on a channel whose phone number is verified.</summary>
    public async Task SetThumbnailAsync(string token, string videoId, string picture, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://www.googleapis.com/upload/youtube/v3/thumbnails/set?videoId={Uri.EscapeDataString(videoId)}")
        {
            Content = new ByteArrayContent(await File.ReadAllBytesAsync(picture, ct)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var reply = await send(request, ct);
        await JsonAsync(reply, ct);
    }

    /// <summary>The reply's JSON; YouTube's or Google's own words on what was wrong when it is a refusal.</summary>
    private static async Task<JsonNode> JsonAsync(HttpResponseMessage reply, CancellationToken ct)
    {
        var text = await reply.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try
        {
            json = text.Length > 0 ? JsonNode.Parse(text) : null;
        }
        catch (JsonException)
        {
            // Not JSON: said plainly below if it was a refusal.
        }
        if (!reply.IsSuccessStatusCode)
        {
            var said = json?["error"] is JsonObject error ? error["message"]?.GetValue<string>() : json?["error_description"]?.GetValue<string>() ?? json?["error"]?.GetValue<string>();
            throw new InvalidOperationException($"YouTube said no ({(int)reply.StatusCode}): {said ?? reply.ReasonPhrase}");
        }
        return json ?? new JsonObject();
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A file sent as it is read, telling how far it has got.</summary>
    private sealed class FileContent(string path, IProgress<double>? progress) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await using var file = File.OpenRead(path);
            var buffer = new byte[1 << 20];
            long sent = 0;
            int read;
            while ((read = await file.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                progress?.Report((double)(sent += read) / Math.Max(1, file.Length));
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = new FileInfo(path).Length;
            return true;
        }
    }
}
