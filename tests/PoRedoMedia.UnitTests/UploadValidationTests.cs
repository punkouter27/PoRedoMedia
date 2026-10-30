using PoRedoMedia.Api.Features.Media;
using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.UnitTests;

public sealed class UploadValidationTests
{
    [Fact]
    public void An_accepted_file_is_classified_by_its_extension()
    {
        foreach (var (name, size, kind, extension, contentType) in new[]
        {
            ("beach.JPG", 1_000L, MediaKind.Image, ".jpg", "image/jpeg"),
            ("a.b.png", 1_000L, MediaKind.Image, ".png", "image/png"),
            ("party.mp4", 50_000_000L, MediaKind.Video, ".mp4", "video/mp4"),
            ("clip.mov", 1_000L, MediaKind.Video, ".mov", "video/quicktime"),
        })
        {
            var result = UploadValidation.Classify(name, size);

            Assert.Null(result.Error);
            Assert.Equal((kind, extension, contentType), (result.Kind, result.Extension, result.ContentType));
        }
    }

    [Fact]
    public void A_refused_file_gets_a_reason_that_states_the_limit()
    {
        foreach (var (name, size, reasonContains) in new[]
        {
            ("virus.exe", 1_000L, "type"),
            ("noextension", 1_000L, "type"),
            ("big.png", 10 * 1024 * 1024 + 1L, "10 MB"),
            ("big.mp4", 200L * 1024 * 1024 + 1, "200 MB"),
            ("empty.png", 0L, "empty"),
        })
        {
            Assert.Contains(reasonContains, UploadValidation.Classify(name, size).Error);
        }
    }
}
