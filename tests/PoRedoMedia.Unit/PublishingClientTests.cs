using System.Net;
using PoRedoMedia.Api.Features.Publishing;
using PoRedoMedia.Shared.Models;
using PoRedoMedia.Shared.Validation;

namespace PoRedoMedia.UnitTests;

/// <summary>The YouTube and TikTok clients against fake sites: nothing here reaches Google or TikTok.</summary>
public sealed class PublishingClientTests
{
    /// <summary>Answers each request in turn and keeps what was asked.</summary>
    private sealed class FakeSite(params Func<HttpRequestMessage, HttpResponseMessage>[] replies) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Range, long Length)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Seen.Add((request.Method, request.RequestUri!, request.Content?.Headers.ContentRange?.ToString(), body.Length));
            return replies[Math.Min(Seen.Count - 1, replies.Length - 1)](request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json) };

    [Fact]
    public async Task Uploads_follow_each_sites_protocol_and_refusals_say_what_to_do()
    {
        // YouTube: a resumable session, 8 MiB chunks (a multiple of 256 KiB) answered 308 with what arrived, then 201.
        var video = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(video, new byte[GoogleYouTubePublisher.ChunkSize + 1000]);
        try
        {
            var session = new Uri("https://upload.example/session");
            var google = new FakeSite(
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Headers = { Location = session }, Content = new StringContent("") },
                _ => new HttpResponseMessage((HttpStatusCode)308) { Headers = { { "Range", $"bytes=0-{GoogleYouTubePublisher.ChunkSize - 1}" } } },
                _ => Json(HttpStatusCode.Created, """{"id":"abc123","status":{"privacyStatus":"private"}}"""));
            var youTube = new GoogleYouTubePublisher("id", "secret", () => new HttpClient(google));
            var progress = new List<double>();
            var uploaded = await youTube.UploadAsync("token", video, new YouTubeVideo("Title <b>", "Words", ["a b", "c"], "22", "public", true),
                new Progress<double>(progress.Add), default);
            Assert.Equal(new YouTubeUploaded("abc123", "private"), uploaded); // asked public, kept private: the job says so
            Assert.Equal(0, GoogleYouTubePublisher.ChunkSize % (256 * 1024));
            Assert.Equal($"bytes 0-{GoogleYouTubePublisher.ChunkSize - 1}/{GoogleYouTubePublisher.ChunkSize + 1000}", google.Seen[1].Range);
            Assert.Equal($"bytes {GoogleYouTubePublisher.ChunkSize}-{GoogleYouTubePublisher.ChunkSize + 999}/{GoogleYouTubePublisher.ChunkSize + 1000}", google.Seen[2].Range);
            Assert.Equal(session, google.Seen[2].Uri);
            Assert.Contains("access_type=offline", youTube.SignInPage("https://app/cb", "s").Query, StringComparison.Ordinal);

            // TikTok: one chunk under 5 MB, else 10 MB chunks with the rest in the last; a draft goes to the inbox.
            Assert.Equal((4_000_000L, 1), TikTokPublisher.ChunkPlan(4_000_000));
            Assert.Equal((10_000_000L, 2), TikTokPublisher.ChunkPlan(29_999_999));
            var tikTokSite = new FakeSite(
                _ => Json(HttpStatusCode.OK, """{"data":{"publish_id":"p1","upload_url":"https://upload.example/tt"},"error":{"code":"ok"}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.Created),
                _ => Json(HttpStatusCode.OK, """{"data":{"status":"SEND_TO_USER_INBOX"},"error":{"code":"ok"}}"""));
            var tikTok = new TikTokPublisher("key", "secret", directPost: false, () => new HttpClient(tikTokSite));
            Assert.Equal(new TikTokSent("p1", "SEND_TO_USER_INBOX"), await tikTok.SendAsync("token", video, null, null, null, default));
            Assert.EndsWith("/inbox/video/init/", tikTokSite.Seen[0].Uri.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal($"bytes 0-{GoogleYouTubePublisher.ChunkSize + 999}/{GoogleYouTubePublisher.ChunkSize + 1000}", tikTokSite.Seen[1].Range);
            Assert.DoesNotContain("video.publish", tikTok.SignInPage("https://app/cb", "s").Query, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(video);
        }

        // Refusals, sorted: connect again, try tomorrow, or what the site said.
        Assert.Equal(PublishFailure.Quota, GoogleYouTubePublisher.Failure(HttpStatusCode.Forbidden,
            (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""{"error":{"errors":[{"reason":"quotaExceeded"}],"message":"q"}}""")!).Failure);
        Assert.Equal(PublishFailure.Reconnect, GoogleYouTubePublisher.Failure(HttpStatusCode.BadRequest,
            (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""{"error":"invalid_grant"}""")!).Failure);
        Assert.Contains("Only me", TikTokPublisher.Failure("unaudited_client_can_only_post_to_private_accounts", 403).Message, StringComparison.Ordinal);
        Assert.Equal(PublishFailure.Reconnect, TikTokPublisher.Failure("access_token_invalid", 401).Failure);

        // The dialog's rules: something picked, and only what is connected.
        var connected = new PublishStatusDto(new(true, true), new(true, false));
        var rules = new PublishRequestValidator();
        Assert.True(rules.Validate(new PublishRequest(true, "private", false, "draft", "SELF_ONLY"), connected).IsValid);
        Assert.Equal("Pick at least one place to publish to.", Assert.Single(rules.Validate(new PublishRequest(false, "private", false, "draft", "SELF_ONLY"), connected).Errors).ErrorMessage);
        Assert.Equal("Connect TikTok first.", Assert.Single(rules.Validate(new PublishRequest(false, "private", true, "draft", "SELF_ONLY"), connected).Errors).ErrorMessage);
    }
}
