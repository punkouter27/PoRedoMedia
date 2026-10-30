using System.Text.Json;
using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Common.Ai;
using SkiaSharp;

namespace PoRedoMedia.UnitTests;

public sealed class GeminiImageServiceTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void The_picture_is_read_from_the_first_image_part()
    {
        var image = GeminiImageService.Read(Json(
            """{"candidates":[{"content":{"parts":[{"inlineData":{"mimeType":"image/jpeg","data":"AQID"}}]}}]}"""));

        Assert.Equal([1, 2, 3], image.Data);
        Assert.Equal(("image/jpeg", ".jpg"), (image.ContentType, image.Extension));
    }

    [Fact]
    public void A_refusal_becomes_a_failure_the_user_can_read()
    {
        foreach (var (response, reasonContains) in new[]
        {
            ("""{"candidates":[{"finishReason":"SAFETY"}]}""", "safety filter"),
            ("""{"candidates":[{"content":{"parts":[{"text":"REFUSE: I cannot draw that."}]}}]}""", "declined: I cannot draw that."),
            ("""{"candidates":[{"content":{"parts":[]}}]}""", "returned no picture"),
            ("""{"promptFeedback":{"blockReason":"OTHER"}}""", "declined this request"),
        })
        {
            var error = Assert.Throws<RunStepException>(() => GeminiImageService.Read(Json(response)));

            Assert.Contains(reasonContains, error.Message);
        }
    }

    [Fact]
    public void The_new_picture_is_asked_for_in_the_nearest_supported_shape()
    {
        foreach (var (width, height, expected) in new[] { (800, 600, "4:3"), (600, 800, "3:4"), (1000, 1000, "1:1"), (1920, 1080, "16:9") })
        {
            using var image = new SKBitmap(width, height);

            Assert.Equal(expected, GeminiImageService.AspectRatioOf(ImageBytes.ToPng(image)));
        }

        Assert.Null(GeminiImageService.AspectRatioOf([1, 2, 3]));
    }
}
