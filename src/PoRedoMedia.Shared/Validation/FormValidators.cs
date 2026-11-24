using FluentValidation;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Shared.Validation;

/// <summary>
/// Comic-ify's form: the pasted conversation. The page shows these messages under the field and the API answers them
/// as a validation problem, so both say the same thing.
/// </summary>
public sealed class ComicRequestValidator : AbstractValidator<ComicRequest>
{
    public ComicRequestValidator()
    {
        RuleFor(r => r.Conversation)
            .Must(c => Cleaned(c).Length >= ComicRequest.MinLength)
            .WithMessage("Paste a conversation of at least a couple of lines.")
            .Must(c => Cleaned(c).Length <= ComicRequest.MaxLength)
            .WithMessage($"That is too long for one comic: keep it under {ComicRequest.MaxLength:N0} characters.");
        RuleFor(r => r.Mix).MaximumLength(64);
        RuleFor(r => r.Narrator).MaximumLength(64);
    }

    /// <summary>The conversation as the server reads it: Windows line ends made plain and the ends trimmed.</summary>
    public static string Cleaned(string? conversation) =>
        (conversation ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}

/// <summary>The server's limits for a movie (its <see cref="MovieConfigDto"/>), passed to each check as context data.</summary>
public sealed record MovieLimits(int MaxFilms, int MaxTopicLength);

/// <summary>
/// Create Movie's "make the video from my films" form. Limits that depend on the server (how many films, how long a
/// topic) come from its <see cref="MovieConfigDto"/> as <see cref="MovieLimits"/> context data
/// (<see cref="ContextData.Validate{T}"/>), so the page and the API pass the same numbers to one registered validator.
/// </summary>
public sealed class MovieLessonRequestValidator : AbstractValidator<MovieLessonRequest>
{
    public const int MaxTitle = 120, MaxSecond = 2000, MaxNote = 200;

    public MovieLessonRequestValidator()
    {
        RuleFor(r => r.Films).Cascade(CascadeMode.Stop).NotEmpty().WithMessage("Add a film first.")
            .Custom((films, context) =>
            {
                var most = ContextData.Get<MovieLimits, MovieLessonRequest>(context).MaxFilms;
                if (films.Length > most)
                    context.AddFailure($"A movie can be made from up to {most} films. Remove {films.Length - most} and try again.");
            });
        RuleForEach(r => r.Films).Must(f => (f.Note ?? "").Length <= MaxNote)
            .WithMessage($"A film's note can be up to {MaxNote} characters.");
        RuleFor(r => r.Title).MaximumLength(MaxTitle).WithMessage($"A title can be up to {MaxTitle} characters.");
        RuleFor(r => r.Number).InclusiveBetween(1, 999).WithMessage("The episode number is 1 to 999.");
        RuleFor(r => r.Topic).Custom((topic, context) =>
        {
            var most = ContextData.Get<MovieLimits, MovieLessonRequest>(context).MaxTopicLength;
            if ((topic ?? "").Length > most)
                context.AddFailure($"Say what it is about in up to {most:N0} characters.");
        });
        RuleFor(r => r.Second).MaximumLength(MaxSecond).WithMessage($"Keep this to {MaxSecond:N0} characters.");
    }
}

/// <summary>Create Movie's YouTube upload form.</summary>
public sealed class MovieUploadRequestValidator : AbstractValidator<MovieUploadRequest>
{
    public const int MaxTitle = 100, MaxDescription = 5000;

    public static readonly string[] Privacies = ["Private", "Unlisted", "Public"];

    public MovieUploadRequestValidator(TimeProvider? clock = null)
    {
        var time = clock ?? TimeProvider.System;
        RuleFor(r => r.Replace).Must(link => string.IsNullOrWhiteSpace(link) || YouTubeLinks.VideoId(link) is not null)
            .WithMessage("Paste the link of a video on your channel, such as https://youtu.be/… or its YouTube Studio link.");
        RuleFor(r => r.Replace).Must(link => !string.IsNullOrWhiteSpace(link)).When(r => r.DeleteOnly)
            .WithMessage("Paste the link of the video to delete.");
        RuleFor(r => r.Replace).Must(string.IsNullOrWhiteSpace).When(r => r.Shorts && !r.DeleteOnly)
            .WithMessage("Replacing works for the finished video, not for Shorts. Empty this box to upload the teasers.");
        // Deleting uploads nothing, so the details of an upload are not asked for.
        When(r => !r.DeleteOnly, () =>
        {
            RuleFor(r => r.Title).Must(t => !string.IsNullOrWhiteSpace(t)).WithMessage("Give the video a title.")
                .MaximumLength(MaxTitle).WithMessage($"YouTube takes a title of up to {MaxTitle} characters.");
            RuleFor(r => r.Description).MaximumLength(MaxDescription).WithMessage($"YouTube takes a description of up to {MaxDescription:N0} characters.");
            RuleFor(r => r.Privacy).Must(p => Privacies.Contains(p)).WithMessage("Choose who can watch it.");
            RuleFor(r => r.PublishAt).Must(at => at is null || at > time.GetUtcNow()).WithMessage("Pick a time that has not passed yet.");
        });
    }
}
