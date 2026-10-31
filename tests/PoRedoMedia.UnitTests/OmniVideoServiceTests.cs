using System.Text.Json;
using PoRedoMedia.Api.Features.PhotoToVideo;

namespace PoRedoMedia.UnitTests;

public sealed class OmniVideoServiceTests
{
    [Fact]
    public void The_clip_is_read_from_a_completed_interaction_and_missing_when_it_was_blocked()
    {
        using var finished = JsonDocument.Parse(
            """{"status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"\n"}]},{"type":"thought","signature":"x"},{"type":"model_output","content":[{"type":"video","mime_type":"video/mp4","data":"AAEC"}]}]}""");
        using var blocked = JsonDocument.Parse("""{"status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"I can't make that."}]}]}""");

        Assert.True(OmniVideoService.TryExtractVideo(finished.RootElement, out var clip));
        Assert.Equal(new byte[] { 0, 1, 2 }, clip);
        Assert.False(OmniVideoService.TryExtractVideo(blocked.RootElement, out _));
    }
}
