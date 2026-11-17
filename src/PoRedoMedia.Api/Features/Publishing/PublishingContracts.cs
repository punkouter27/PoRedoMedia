namespace PoRedoMedia.Api.Features.Publishing;

/// <summary>The sites a movie can be published to.</summary>
public enum Platform { YouTube, TikTok }

/// <summary>What a sign-in gave: a short-lived pass to call with, and the long-lived one kept (encrypted) to get more.</summary>
/// <param name="Refresh">Null when the site gave none (Google gives one only on a first, or forced, consent).</param>
public sealed record PlatformTokens(string Access, string? Refresh);

/// <summary>Why a site refused, sorted so the app can say what to do about it.</summary>
public enum PublishFailure
{
    /// <summary>The connection was revoked or expired: connect again.</summary>
    Reconnect,

    /// <summary>The site's daily allowance (YouTube quota, TikTok post limits) is used up: try tomorrow.</summary>
    Quota,

    /// <summary>The site turned the video or its details away.</summary>
    Refused,

    /// <summary>The site could not be reached or broke off: try again.</summary>
    Unavailable,
}

/// <summary>A site's refusal, in words fit to show, with what kind it is.</summary>
public sealed class PublishException(PublishFailure failure, string message) : Exception(message)
{
    public PublishFailure Failure { get; } = failure;
}

/// <summary>What YouTube is sent with a video.</summary>
/// <param name="Privacy">"private", "unlisted" or "public".</param>
public sealed record YouTubeVideo(string Title, string Description, IReadOnlyList<string> Tags, string CategoryId, string Privacy, bool MadeForKids);

/// <summary>What came of a YouTube upload.</summary>
/// <param name="Privacy">The privacy YouTube actually gave it, which can differ from the one asked for.</param>
public sealed record YouTubeUploaded(string VideoId, string Privacy);

/// <summary>
/// YouTube Data API v3 for one app (OAuth web-server flow with offline access, resumable upload, thumbnails.set).
/// Behind an interface so tests and the Test environment use a fake and nothing reaches Google.
/// </summary>
public interface IYouTubePublisher
{
    /// <summary>False when the app's Google OAuth client is not configured.</summary>
    bool Configured { get; }

    Uri SignInPage(string redirect, string state);

    Task<PlatformTokens> ExchangeAsync(string code, string redirect, CancellationToken ct);

    Task<string> RefreshAsync(string refreshToken, CancellationToken ct);

    Task RevokeAsync(string token, CancellationToken ct);

    Task<YouTubeUploaded> UploadAsync(string access, string videoPath, YouTubeVideo video, IProgress<double>? progress, CancellationToken ct);

    Task SetThumbnailAsync(string access, string videoId, string jpegPath, CancellationToken ct);
}

/// <summary>What came of sending a video to TikTok.</summary>
/// <param name="Status">TikTok's last word on it, such as SEND_TO_USER_INBOX or PUBLISH_COMPLETE.</param>
public sealed record TikTokSent(string PublishId, string Status);

/// <summary>
/// TikTok's Login Kit (OAuth v2) and Content Posting API: FILE_UPLOAD in chunks, to the user's inbox as a draft, or a
/// direct post where the app has video.publish. Behind an interface so tests use a fake and nothing reaches TikTok.
/// </summary>
public interface ITikTokPublisher
{
    /// <summary>False when the app's TikTok client key and secret are not configured.</summary>
    bool Configured { get; }

    /// <summary>The app is set up for direct posts (it has the video.publish scope).</summary>
    bool DirectPost { get; }

    Uri SignInPage(string redirect, string state);

    Task<PlatformTokens> ExchangeAsync(string code, string redirect, CancellationToken ct);

    /// <summary>A new access token; TikTok rotates the refresh token, so the new one comes back too.</summary>
    Task<PlatformTokens> RefreshAsync(string refreshToken, CancellationToken ct);

    Task RevokeAsync(string token, CancellationToken ct);

    /// <summary>The privacy levels this creator may post with (an unaudited app gets SELF_ONLY only).</summary>
    Task<IReadOnlyList<string>> PrivacyOptionsAsync(string access, CancellationToken ct);

    /// <param name="caption">Null for a draft to the inbox (the user writes the caption in TikTok); else a direct post.</param>
    Task<TikTokSent> SendAsync(string access, string videoPath, string? caption, string? privacy, IProgress<double>? progress, CancellationToken ct);
}
