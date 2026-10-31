using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Features.Video;

namespace PoRedoMedia.UnitTests.Video;

public sealed class MemeSoundChoiceTests
{
    private static SoundAsset Sound(string name, int popularity, params string[] tags) =>
        new() { DisplayName = name, BlobUrl = $"sounds/{name}.mp3", ActionVectorTags = tags, Popularity = popularity };

    [Fact]
    public void A_famous_sound_wins_an_equal_match_but_never_one_it_does_not_fit_and_the_menu_says_it_is_famous()
    {
        var bruh = Sound("BRUH", 2, "fail", "disapproval");
        var obscure = Sound("Sad trombone remix", 240, "fail", "disapproval");
        var boom = Sound("VINE BOOM", 1, "impact", "boom");
        var library = new[] { obscure, bruh, boom };

        var ranked = new SemanticMatchingService().GetTopCandidatesBatch(library, ["a total fail, pure disapproval"], topN: 3)[0];

        // Same fit, so the famous one comes first; the most famous sound of all shares no word with
        // the moment and is not offered for it.
        Assert.Equal(["BRUH", "Sad trombone remix"], ranked.Select(c => c.Sound.DisplayName));
        Assert.Contains("| famous #2", DirectorPrompt.SerializeSoundsCompact([bruh, obscure]), StringComparison.Ordinal);
        Assert.DoesNotContain("famous #240", DirectorPrompt.SerializeSoundsCompact([obscure]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeated_sound_is_swapped_for_the_closest_unused_one_the_famous_one_on_a_tie()
    {
        var bruh = Sound("BRUH", 2, "fail", "disapproval");
        var buzzer = Sound("Buzzer", 3, "fail", "wrong");
        var obscure = Sound("Wah wah remix", 200, "fail", "sad");
        var boom = Sound("VINE BOOM", 1, "impact", "boom");
        ScriptEntry Cue(SoundAsset s) => new() { MediaId = MediaId.New(), SoundId = s.SoundId, SoundName = s.DisplayName, ActionVectorTags = s.ActionVectorTags };
        var entries = new[] { Cue(bruh), Cue(boom), Cue(bruh) };

        var swapped = PlacementPlanner.NoRepeats(entries, [bruh, boom, obscure, buzzer]);

        // Buzzer and the remix share one tag with BRUH; Buzzer is far more famous.
        Assert.Equal(1, swapped);
        Assert.Equal(["BRUH", "VINE BOOM", "Buzzer"], entries.Select(e => e.SoundName));
    }
}
