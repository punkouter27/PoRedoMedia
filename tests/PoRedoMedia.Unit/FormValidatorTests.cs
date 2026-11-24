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
        var rules = new MovieLessonRequestValidator();
        var limits = new MovieLimits(MaxFilms: 2, MaxTopicLength: 10);
        MovieLessonRequest Request(int films, string topic = "", int number = 1) =>
            new("General", "", topic, "", number, [.. Enumerable.Range(0, films).Select(_ => new MovieFilmDto(Guid.NewGuid(), "", false))]);

        Assert.True(rules.Validate(Request(1), limits).IsValid);
        Assert.Equal("Add a film first.", Assert.Single(rules.Validate(Request(0), limits).Errors).ErrorMessage);
        Assert.Contains("Remove 1", Assert.Single(rules.Validate(Request(3), limits).Errors).ErrorMessage);
        Assert.Equal(nameof(MovieLessonRequest.Topic), Assert.Single(rules.Validate(Request(1, topic: "far too long a topic"), limits).Errors).PropertyName);
        Assert.Equal(nameof(MovieLessonRequest.Number), Assert.Single(rules.Validate(Request(1, number: 0), limits).Errors).PropertyName);
    }

    [Fact]
    public void A_movie_check_without_the_servers_limits_says_what_is_missing()
    {
        var rules = new MovieLessonRequestValidator();

        var missing = Assert.Throws<InvalidOperationException>(() =>
            rules.Validate(new MovieLessonRequest("General", "", "", "", 1, [new MovieFilmDto(Guid.NewGuid(), "", false)])));
        Assert.Contains(nameof(MovieLimits), missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_api_and_the_client_register_every_form_rule_by_type()
    {
        foreach (var source in new[] { "src/PoRedoMedia.Api/Program.cs", "src/PoRedoMedia.Client/Program.cs" })
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), source));
            foreach (var (model, validator) in new[] { ("ComicRequest", "ComicRequestValidator"), ("MovieLessonRequest", "MovieLessonRequestValidator"), ("MovieUploadRequest", "MovieUploadRequestValidator"), ("PublishRequest", "PublishRequestValidator") })
                Assert.Contains($"AddScoped<IValidator<{model}>, {validator}>()", text, StringComparison.Ordinal);
            Assert.DoesNotContain("AddValidatorsFrom", text, StringComparison.Ordinal);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PoRedoMedia.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("PoRedoMedia.slnx not found above the test output.");
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

    [Fact]
    public void A_replace_or_delete_needs_a_link_to_a_youtube_video_and_a_delete_needs_nothing_else()
    {
        var rules = new MovieUploadRequestValidator();

        Assert.True(rules.Validate(new MovieUploadRequest("Treehouse Day", "", "", "Private", null, false, "https://youtu.be/dQw4w9WgXcQ")).IsValid);
        Assert.True(rules.Validate(new MovieUploadRequest("", "", "", "", null, false, "https://youtu.be/dQw4w9WgXcQ", DeleteOnly: true)).IsValid);
        Assert.Equal([nameof(MovieUploadRequest.Replace)],
            rules.Validate(new MovieUploadRequest("Treehouse Day", "", "", "Private", null, false, "https://www.youtube.com/@channel")).Errors.Select(e => e.PropertyName));
        Assert.Equal([nameof(MovieUploadRequest.Replace)],
            rules.Validate(new MovieUploadRequest("", "", "", "", null, false, null, DeleteOnly: true)).Errors.Select(e => e.PropertyName));
        Assert.Equal([nameof(MovieUploadRequest.Replace)],
            rules.Validate(new MovieUploadRequest("Teasers", "", "", "Private", null, true, "https://youtu.be/dQw4w9WgXcQ")).Errors.Select(e => e.PropertyName));
    }
}
