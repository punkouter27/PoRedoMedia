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

/// <summary>
/// Create Movie's "make the video from my films" form. Limits that depend on the server (how many films, how long a
/// topic) come from its <see cref="MovieConfigDto"/>, so the page and the API pass the same numbers.
/// </summary>
public sealed class MovieLessonRequestValidator : AbstractValidator<MovieLessonRequest>
{
    public const int MaxTitle = 120, MaxSecond = 2000, MaxNote = 200;

    public MovieLessonRequestValidator(int maxFilms, int maxTopicLength)
    {
        RuleFor(r => r.Films).Cascade(CascadeMode.Stop).NotEmpty().WithMessage("Add a film first.")
            .Must(f => f.Length <= maxFilms)
            .WithMessage(r => $"A movie can be made from up to {maxFilms} films. Remove {r.Films.Length - maxFilms} and try again.");
        RuleForEach(r => r.Films).Must(f => (f.Note ?? "").Length <= MaxNote)
            .WithMessage($"A film's note can be up to {MaxNote} characters.");
        RuleFor(r => r.Title).MaximumLength(MaxTitle).WithMessage($"A title can be up to {MaxTitle} characters.");
        RuleFor(r => r.Number).InclusiveBetween(1, 999).WithMessage("The episode number is 1 to 999.");
        RuleFor(r => r.Topic).MaximumLength(maxTopicLength).WithMessage($"Say what it is about in up to {maxTopicLength:N0} characters.");
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
        RuleFor(r => r.Title).Must(t => !string.IsNullOrWhiteSpace(t)).WithMessage("Give the video a title.")
            .MaximumLength(MaxTitle).WithMessage($"YouTube takes a title of up to {MaxTitle} characters.");
        RuleFor(r => r.Description).MaximumLength(MaxDescription).WithMessage($"YouTube takes a description of up to {MaxDescription:N0} characters.");
        RuleFor(r => r.Privacy).Must(p => Privacies.Contains(p)).WithMessage("Choose who can watch it.");
        RuleFor(r => r.PublishAt).Must(at => at is null || at > time.GetUtcNow()).WithMessage("Pick a time that has not passed yet.");
    }
}
