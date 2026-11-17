using PoRedoMedia.Shared.Models;
using PoRedoMedia.Shared.Validation;

namespace PoRedoMedia.UnitTests;

/// <summary>The form rules the pages check before sending and the API checks again (Shared/Validation).</summary>
public sealed class FormValidatorTests
{
    [Fact]
    public void A_comic_needs_a_conversation_of_a_couple_of_lines_and_not_too_much()
    {
        var rules = new ComicRequestValidator();

        Assert.True(rules.Validate(new ComicRequest("Maya: did you eat my leftovers\nSam: define eat")).IsValid);
        var tooShort = rules.Validate(new ComicRequest("  \r\n hi \r\n "));
        Assert.Equal(nameof(ComicRequest.Conversation), Assert.Single(tooShort.Errors).PropertyName);
        Assert.False(rules.Validate(new ComicRequest(new string('a', ComicRequest.MaxLength + 1))).IsValid);
    }

    [Fact]
    public void A_movie_needs_films_within_the_servers_limits()
    {
        var rules = new MovieLessonRequestValidator(maxFilms: 2, maxTopicLength: 10);
        MovieLessonRequest Request(int films, string topic = "", int number = 1) =>
            new("General", "", topic, "", number, [.. Enumerable.Range(0, films).Select(_ => new MovieFilmDto(Guid.NewGuid(), "", false))]);

        Assert.True(rules.Validate(Request(1)).IsValid);
        Assert.Equal("Add a film first.", Assert.Single(rules.Validate(Request(0)).Errors).ErrorMessage);
        Assert.Contains("Remove 1", Assert.Single(rules.Validate(Request(3)).Errors).ErrorMessage);
        Assert.Equal(nameof(MovieLessonRequest.Topic), Assert.Single(rules.Validate(Request(1, topic: "far too long a topic")).Errors).PropertyName);
        Assert.Equal(nameof(MovieLessonRequest.Number), Assert.Single(rules.Validate(Request(1, number: 0)).Errors).PropertyName);
    }

    [Fact]
    public void An_upload_needs_a_title_a_known_privacy_and_a_time_still_to_come()
    {
        var rules = new MovieUploadRequestValidator();

        Assert.True(rules.Validate(new MovieUploadRequest("Treehouse Day", "", "", "Private", null, false)).IsValid);
        var wrong = rules.Validate(new MovieUploadRequest(" ", "", "", "Everyone", DateTimeOffset.UtcNow.AddDays(-1), false));
        Assert.Equal(
            [nameof(MovieUploadRequest.Title), nameof(MovieUploadRequest.Privacy), nameof(MovieUploadRequest.PublishAt)],
            wrong.Errors.Select(e => e.PropertyName));
    }
}
