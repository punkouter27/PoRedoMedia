namespace PoRedoMedia.Api.Features.Publishing;

/// <summary>
/// Stand-in YouTube for the Test environment and tests (mock AI on): the sign-in "page" sends the browser straight
/// back to the callback with a code, and an upload reads the file, reports progress and gives a made-up id. Nothing
/// reaches Google.
/// </summary>
public sealed class FakeYouTubePublisher : IYouTubePublisher
{
    /// <summary>Every video "uploaded", newest last, for tests to look at.</summary>
    public List<(YouTubeVideo Video, long Bytes)> Uploads { get; } = [];

    public List<string> Revoked { get; } = [];

    /// <summary>When set, the next upload fails with this.</summary>
    public PublishException? FailNext { get; set; }

    public bool Configured => true;

    public Uri SignInPage(string redirect, string state) => new($"{redirect}?code=fake-google-code&state={Uri.EscapeDataString(state)}");

    public Task<PlatformTokens> ExchangeAsync(string code, string redirect, CancellationToken ct) =>
        Task.FromResult(new PlatformTokens("fake-google-access", $"fake-google-refresh-{code}"));

    public Task<string> RefreshAsync(string refreshToken, CancellationToken ct) => Task.FromResult("fake-google-access");

    public Task RevokeAsync(string token, CancellationToken ct)
    {
        lock (Revoked) Revoked.Add(token);
        return Task.CompletedTask;
    }

    public Task<YouTubeUploaded> UploadAsync(string access, string videoPath, YouTubeVideo video, IProgress<double>? progress, CancellationToken ct)
    {
        if (FailNext is { } failure)
        {
            FailNext = null;
            throw failure;
        }
        var bytes = new FileInfo(videoPath).Length;
        lock (Uploads) Uploads.Add((video, bytes));
        progress?.Report(0.5);
        progress?.Report(1);
        return Task.FromResult(new YouTubeUploaded($"fake{Guid.NewGuid():N}"[..11], video.Privacy));
    }

    public Task SetThumbnailAsync(string access, string videoId, string jpegPath, CancellationToken ct) =>
        File.Exists(jpegPath) ? Task.CompletedTask : throw new PublishException(PublishFailure.Refused, "No thumbnail.");
}

/// <summary>Stand-in TikTok for the Test environment and tests: drafts and direct posts, private only (as an unaudited app).</summary>
public sealed class FakeTikTokPublisher : ITikTokPublisher
{
    public List<(string? Caption, string? Privacy, long Bytes)> Sent { get; } = [];

    public List<string> Revoked { get; } = [];

    public bool Configured => true;

    public bool DirectPost => true;

    public Uri SignInPage(string redirect, string state) => new($"{redirect}?code=fake-tiktok-code&state={Uri.EscapeDataString(state)}");

    public Task<PlatformTokens> ExchangeAsync(string code, string redirect, CancellationToken ct) =>
        Task.FromResult(new PlatformTokens("fake-tiktok-access", "fake-tiktok-refresh-1"));

    public Task<PlatformTokens> RefreshAsync(string refreshToken, CancellationToken ct) =>
        Task.FromResult(new PlatformTokens("fake-tiktok-access", refreshToken + "+")); // rotated, as TikTok does

    public Task RevokeAsync(string token, CancellationToken ct)
    {
        lock (Revoked) Revoked.Add(token);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> PrivacyOptionsAsync(string access, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["SELF_ONLY"]);

    public Task<TikTokSent> SendAsync(string access, string videoPath, string? caption, string? privacy, IProgress<double>? progress, CancellationToken ct)
    {
        lock (Sent) Sent.Add((caption, privacy, new FileInfo(videoPath).Length));
        progress?.Report(1);
        return Task.FromResult(new TikTokSent($"v_inbox_file~{Guid.NewGuid():N}", caption is null ? "SEND_TO_USER_INBOX" : "PUBLISH_COMPLETE"));
    }
}
