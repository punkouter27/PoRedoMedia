using FluentValidation;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Shared.Validation;

/// <summary>
/// The Publish dialog's rules, which POST /api/movies/{id}/publish runs too. What is connected is passed in as
/// <see cref="PublishStatusDto"/> context data (<see cref="ContextData.Validate{T}"/>), so the dialog and the server
/// refuse the same things: a site not set up or not connected cannot be picked.
/// </summary>
public sealed class PublishRequestValidator : AbstractValidator<PublishRequest>
{
    public static readonly string[] YouTubePrivacies = ["private", "unlisted", "public"];

    public static readonly string[] TikTokModes = ["draft", "direct"];

    /// <summary>TikTok's privacy levels for a direct post. An app TikTok has not audited may use only SELF_ONLY.</summary>
    public static readonly string[] TikTokPrivacies = ["SELF_ONLY", "MUTUAL_FOLLOW_FRIENDS", "FOLLOWER_OF_CREATOR", "PUBLIC_TO_EVERYONE"];

    public PublishRequestValidator()
    {
        RuleFor(r => r.YouTube).Must((r, _) => r.YouTube || r.TikTok).WithMessage("Pick at least one place to publish to.");
        RuleFor(r => r.YouTube).Must((_, on, context) => !on || Status(context).YouTube is { Configured: true, Connected: true })
            .WithMessage("Connect YouTube first.");
        RuleFor(r => r.TikTok).Must((_, on, context) => !on || Status(context).TikTok is { Configured: true, Connected: true })
            .WithMessage("Connect TikTok first.");
        RuleFor(r => r.YouTubePrivacy).Must(p => YouTubePrivacies.Contains(p)).When(r => r.YouTube)
            .WithMessage("Choose who can watch it on YouTube.");
        RuleFor(r => r.TikTokMode).Must(m => TikTokModes.Contains(m)).When(r => r.TikTok)
            .WithMessage("Choose how it goes to TikTok.")
            .Must((_, m, context) => m != "direct" || Status(context).TikTok.DirectPost).When(r => r.TikTok)
            .WithMessage("Posting straight to TikTok is not set up here; send it as a draft.");
        RuleFor(r => r.TikTokPrivacy).Must(p => TikTokPrivacies.Contains(p)).When(r => r.TikTok && r.TikTokMode == "direct")
            .WithMessage("Choose who can see the TikTok post.");
    }

    private static PublishStatusDto Status(ValidationContext<PublishRequest> context) =>
        ContextData.Get<PublishStatusDto, PublishRequest>(context);
}
