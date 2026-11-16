using PoRedoMedia.Api.Features.Video;

using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Features.Render;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.UnitTests;

public sealed class RoastServiceTests
{
    [Fact]
    public void Jokes_AreReadWithTheirAlternativesAndScheduledApartInsideTheClipAndUnderThirtySeconds()
    {
        // Each joke carries its alternative and its moment, for the judge to pick between.
        var jokes = RoastService.ReadJokes(
            """{"jokes":[{"moment":1,"text":"Second joke","alternative":"Second, sharper"},{"moment":0,"text":"First joke","alternative":""}]}""",
            [(1.0, "seen: man slips"), (4.0, "said: watch this")], max: 5);
        Assert.Equal([(1000L, "First joke", (string?)null, "seen: man slips"), (4000L, "Second joke", "Second, sharper", "said: watch this")], jokes);

        // A 40 s clip. Each clip: (its moment, how long it plays).
        var placed = RoastService.Schedule(
            [
                (0, 5_000),       // plays at its moment
                (2_000, 5_000),   // previous joke still running → pushed behind it
                (38_000, 5_000),  // would run past the end → pulled back to end with the clip
                (39_000, 5_000),  // no room left before the end → dropped
            ],
            videoMs: 40_000);

        Assert.Equal([(0, 0L), (1, 5_250L), (2, 35_000L)], placed);

        // Seven 5 s jokes with room for all: the seventh would be second 31–35 of speech.
        var capped = RoastService.Schedule([.. Enumerable.Range(0, 7).Select(i => (i * 10_000L, 5_000))], videoMs: 600_000);
        Assert.Equal(6, capped.Count);

        Assert.Contains("vulgar and profane", RoastService.PromptFor(crude: true));
        Assert.Contains("without any swearing", RoastService.PromptFor(crude: false));
        Assert.Contains("Profanity is expected", RoastService.RubricFor(crude: true));
        Assert.Contains("no profanity", RoastService.RubricFor(crude: false));

        // The sliders: meanness and swearing each change the brief, roast lines the joke count.
        Assert.Contains("gentle, good-natured", RoastService.PromptFor(MemeMix.Default with { Meanness = 0 }));
        Assert.Contains("fake praise", RoastService.PromptFor(MemeMix.Kids));
        Assert.Contains("mild words only", RoastService.PromptFor(MemeMix.Default with { Swearing = 1 }));
        Assert.Contains("Some swearing is fine", RoastService.RubricFor(MemeMix.Default with { Swearing = 2 }));
        Assert.Equal(0, RoastService.MaxJokesFor(MemeMix.Default with { Jokes = 0 }, 60));
        Assert.Equal(3, RoastService.MaxJokesFor(MemeMix.Default with { Jokes = 1 }, 60));
        Assert.Equal(5, RoastService.MaxJokesFor(MemeMix.Default, 60));
        Assert.Equal(8, RoastService.MaxJokesFor(MemeMix.Default with { Jokes = 4 }, 60));

        // Meme sounds and pictures are thinned evenly; a cue left with neither is dropped.
        RenderVisualEntry[] cues = [.. Enumerable.Range(0, 10).Select(i => new RenderVisualEntry(i * 1000L, $"s{i}.mp3", null, $"CAP {i}", "Top", DurationMs: 500))];
        Assert.Equal(10, MemeAmounts.Apply(cues, MemeMix.Default).Count(c => c.SoundPath.Length > 0));
        var few = MemeAmounts.Apply(cues, MemeMix.Default with { Sounds = 1, Pictures = 0 });
        Assert.Equal(3, few.Count);
        Assert.All(few, c => Assert.Null(c.CaptionText));
        var quiet = MemeAmounts.Apply(cues, MemeMix.Default with { Sounds = 0 });
        Assert.Equal(10, quiet.Count); // captions still show
        Assert.All(quiet, c => Assert.Equal("", c.SoundPath));
        Assert.Empty(MemeAmounts.Apply(cues, MemeMix.Default with { Sounds = 0, Pictures = 0 }));
        Assert.Equal(MemeMix.Kids, MemeMix.Parse(MemeMix.Kids.Encode()));
        // Max meme sounds plans more: no gap over five seconds instead of ten.
        var sound = new SoundAsset { SoundId = SoundId.New(), DisplayName = "Bruh", BlobUrl = "bruh.mp3" };
        Assert.True(new TokenBucketTimingService().Apply([], 30, sound, PlacementPlanner.DenseMaxGapMs).Count > new TokenBucketTimingService().Apply([], 30, sound).Count);
        Assert.Null(MemeMix.Parse("12"));
        // The finer settings travel too, and the first five-digit form still reads.
        var full = MemeMix.Kids with { Style = "mlg", SoundVolume = 4, Chaos = 0, Target = "camera", Fame = 0, CaptionSize = 4, CaptionsTop = true, Turns = "interrupt", Laughs = 2 };
        Assert.Equal(full, MemeMix.Parse(full.Encode()));
        Assert.Equal(MemeMix.Default with { Meanness = 4 }, MemeMix.Parse("40332"));
        Assert.Equal(NarratorChoice.Parse("deadpan@1.08"), new NarratorChoice("deadpan", 3));
        Assert.Contains("shaky camera", RoastService.TargetBrief("camera"));

        // Chaos None takes every effect off; Light only the deep-fry.
        RenderVisualEntry[] fried = [new(0, "a.mp3", "DeepFry", null, null, DurationMs: 500), new(3000, "b.mp3", "MotionBlur", null, null, DurationMs: 500)];
        Assert.All(MemeAmounts.Calm(fried, 0), c => Assert.Null(c.VisualEffect));
        Assert.Equal([null, "MotionBlur"], MemeAmounts.Calm(fried, 1).Select(c => c.VisualEffect));

        // Laughs follow the roast lines (every other when light, each when heavy), never over another sound.
        RenderVisualEntry[] roasted = [.. Enumerable.Range(0, 4).Select(i => new RenderVisualEntry(i * 10_000L, $"r{i}.mp3", null, null, null, Voice: true, DurationMs: 3_000))];
        (string, int)[] laughs = [("laugh.mp3", 2_000)];
        Assert.Equal(2, MemeAmounts.AddLaughs(roasted, 1, laughs, 60_000).Count(c => c.SoundPath == "laugh.mp3"));
        var heavy = MemeAmounts.AddLaughs(roasted, 2, laughs, 60_000);
        Assert.Equal([3_150L, 13_150L, 23_150L, 33_150L], heavy.Where(c => c.SoundPath == "laugh.mp3").Select(c => c.TimestampMs));
        Assert.DoesNotContain(MemeAmounts.AddLaughs(roasted, 2, laughs, 34_000), c => c.SoundPath == "laugh.mp3" && c.TimestampMs > 30_000);

        // "Make room": a joke with no pause in the narration is said after it, and the clip ends just after the last sound.
        (long, long)[] narration = [(0, 9_800), (10_000, 20_000)];
        RenderVisualEntry[] joke = [new(5_000, "j.mp3", null, null, null, Voice: true, DurationMs: 4_500)];
        Assert.Empty(TurnTaking.Arrange(joke, 26_000, narration).Cues);
        var roomed = TurnTaking.Arrange(joke, 26_000, narration, tail: true).Cues;
        Assert.Equal(20_200, Assert.Single(roomed).TimestampMs);
        TranscriptSegmentDto[] said = [new(0, 9.8, "one"), new(10, 20, "two")];
        Assert.Equal(25.2, VideoStep.RoomUsed(roomed, said), 3);
        Assert.Equal(20.5, VideoStep.RoomUsed([], said), 3);
    }
}
