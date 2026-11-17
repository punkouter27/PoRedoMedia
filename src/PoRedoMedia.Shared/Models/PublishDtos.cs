namespace PoRedoMedia.Shared.Models;

/// <summary>
/// Where a movie can be published from this server, and whether the signed-in user has connected each. A platform is
/// offered only when its app keys are configured (<see cref="PlatformStatusDto.Configured"/>).
/// </summary>
public sealed record PublishStatusDto(PlatformStatusDto YouTube, PlatformStatusDto TikTok);

/// <param name="Configured">This server has the platform's app keys; false means the owner still has to set it up.</param>
/// <param name="Connected">The signed-in user has connected an account (a token is kept, encrypted).</param>
/// <param name="DirectPost">TikTok only: posting straight to the profile is set up (the app has video.publish).</param>
/// <param name="Setup">What to set up when it is not configured, in words fit to show.</param>
public sealed record PlatformStatusDto(bool Configured, bool Connected, bool DirectPost = false, string? Setup = null);

/// <summary>
/// One press of Publish: which sites, and how. YouTube gets the finished video (title, description, tags and category
/// from the movie's own notes, made for kids for the Kids series) and its thumbnail; TikTok gets the upright 9:16 copy,
/// as a draft in the user's TikTok inbox or, where set up, posted straight to the profile.
/// </summary>
/// <param name="YouTubePrivacy">"private", "unlisted" or "public".</param>
/// <param name="TikTokMode">"draft" (to the TikTok inbox) or "direct".</param>
/// <param name="TikTokPrivacy">For a direct post: "SELF_ONLY", "MUTUAL_FOLLOW_FRIENDS", "FOLLOWER_OF_CREATOR" or "PUBLIC_TO_EVERYONE".</param>
public sealed record PublishRequest(bool YouTube, string YouTubePrivacy, bool TikTok, string TikTokMode, string TikTokPrivacy);
