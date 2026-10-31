using PoRedoMedia.Api.Features.Render;

namespace PoRedoMedia.Api.Features.Video;

/// <summary>
/// Meme sounds and roast lines take turns instead of talking over each other: one speaker, walked
/// along the timeline.
/// <list type="number">
/// <item>When the speaker is free, the next cue plays at its own moment; at the same moment the
/// meme sound goes first and the joke follows it.</item>
/// <item>When cues have piled up behind one still playing, a joke that is due goes first and the
/// meme sounds wait. A sound that would wait more than <see cref="MaxSoundWaitMs"/> no longer fits
/// its moment and is left out.</item>
/// <item>A moved or left-out sound keeps its caption, sticker and effect on its moment; only the
/// sound moves.</item>
/// <item>A song (the rap roast) has no turns to share: meme sounds during it are muted, and their
/// captions, stickers and effects still show.</item>
/// </list>
/// A joke that cannot finish before the video ends is left out.
/// </summary>
public static class TurnTaking
{
    /// <summary>The pause between one sound ending and the next starting.</summary>
    internal const int GapMs = 200;

    /// <summary>A cue of unknown length is taken to last this long.</summary>
    internal const int UnknownMs = 1500;

    /// <summary>A voice line longer than this is a song (the rap roast), not a joke.</summary>
    internal const int SongMs = 15_000;

    /// <summary>A waiting sound must have at least this much video left to start.</summary>
    internal const int MinPlayMs = 500;

    /// <summary>The longest a meme sound waits for its turn before it is left out.</summary>
    internal const int MaxSoundWaitMs = 6000;

    public sealed record Result(List<RenderVisualEntry> Cues, int JokesMoved, int JokesDropped, int SoundsWaited, int SoundsLeftOut);

    public static Result Arrange(IReadOnlyList<RenderVisualEntry> cues, long videoMs)
    {
        if (!cues.Any(c => c.Voice) || !cues.Any(c => !c.Voice))
            return new([.. cues], 0, 0, 0, 0);

        static long Length(RenderVisualEntry c) => c.DurationMs > 0 ? c.DurationMs : UnknownMs;
        var output = new List<RenderVisualEntry>();
        int jokesMoved = 0, jokesDropped = 0, waited = 0, leftOut = 0;

        // A song keeps its place and mutes the meme sounds it covers.
        var songs = cues.Where(c => c.Voice && Length(c) > SongMs).ToList();
        output.AddRange(songs);
        var sounds = new List<RenderVisualEntry>();
        foreach (var sound in cues.Where(c => !c.Voice).OrderBy(c => c.TimestampMs))
        {
            if (songs.Any(s => sound.TimestampMs < s.TimestampMs + Length(s) && sound.TimestampMs + Length(sound) > s.TimestampMs))
            {
                output.Add(WithoutSound(sound));
                leftOut++;
            }
            else
            {
                sounds.Add(sound);
            }
        }

        var jokes = cues.Where(c => c.Voice && Length(c) <= SongMs).OrderBy(c => c.TimestampMs).ToList();
        long free = 0;
        while (sounds.Count + jokes.Count > 0)
        {
            var next = Math.Min(sounds.Count > 0 ? sounds[0].TimestampMs : long.MaxValue, jokes.Count > 0 ? jokes[0].TimestampMs : long.MaxValue);
            var behind = free > next;
            var start = Math.Max(free, next);

            // Free: whatever is next, the sound first on a tie. Behind: a due joke first.
            var jokeTurn = jokes.Count > 0 && jokes[0].TimestampMs <= start
                && (behind || sounds.Count == 0 || sounds[0].TimestampMs > jokes[0].TimestampMs);
            if (jokeTurn)
            {
                var joke = jokes[0];
                jokes.RemoveAt(0);
                if (start + Length(joke) > videoMs)
                {
                    jokesDropped++;
                    continue;
                }

                if (start != joke.TimestampMs)
                    jokesMoved++;
                output.Add(joke with { TimestampMs = start });
                free = start + Length(joke) + GapMs;
                continue;
            }

            var sound = sounds[0];
            sounds.RemoveAt(0);
            if (start - sound.TimestampMs > MaxSoundWaitMs || start + MinPlayMs > videoMs)
            {
                if (HasVisuals(sound))
                    output.Add(WithoutSound(sound));
                leftOut++;
                continue;
            }

            if (start == sound.TimestampMs)
            {
                output.Add(sound);
            }
            else
            {
                if (HasVisuals(sound))
                    output.Add(WithoutSound(sound));
                output.Add(sound with
                {
                    TimestampMs = start, VisualEffect = null, CaptionText = null, CaptionPosition = null, OverlayPath = null,
                });
                waited++;
            }

            free = start + Length(sound) + GapMs;
        }

        return new([.. output.OrderBy(c => c.TimestampMs)], jokesMoved, jokesDropped, waited, leftOut);
    }

    private static bool HasVisuals(RenderVisualEntry cue) =>
        !string.IsNullOrWhiteSpace(cue.CaptionText) || !string.IsNullOrEmpty(cue.OverlayPath)
        || (cue.VisualEffect is { } effect && effect != "None");

    /// <summary>The cue's caption, sticker and effect at its moment, with no sound.</summary>
    private static RenderVisualEntry WithoutSound(RenderVisualEntry cue) => cue with { SoundPath = "", DurationMs = 0 };
}
