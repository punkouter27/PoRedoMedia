
using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Features.Render;
using PoRedoMedia.Api.Features.Video;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.UnitTests;

/// <summary>Reading what was said from the speech model (no subtitles are drawn on videos any more).</summary>
public sealed class TranscriptionReplyTests
{
    [Fact]
    public void A_whisper_reply_becomes_timed_lines_without_the_segments_it_marks_as_not_speech()
    {
        using var reply = System.Text.Json.JsonDocument.Parse("""
            {"duration":8.28,"segments":[
              {"start":0.0,"end":4.32,"text":" Hello and welcome.","no_speech_prob":0.01},
              {"start":4.32,"end":6.0,"text":" [music]","no_speech_prob":0.93},
              {"start":6.0,"end":8.2,"text":"  ","no_speech_prob":0.0}]}
            """);

        var (segments, seconds) = PoRedoMedia.Api.Common.Ai.AiFoundryTranscriptionService.Read(reply.RootElement);

        var only = Assert.Single(segments);
        Assert.Equal((0.0, 4.32, "Hello and welcome."), (only.StartSeconds, only.EndSeconds, only.Text));
        Assert.Equal(8.28, seconds);
    }
}
