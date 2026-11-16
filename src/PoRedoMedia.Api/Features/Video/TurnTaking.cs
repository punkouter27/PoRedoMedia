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
/// A joke that cannot finish before the video ends is left out. Over a narrator, the two never speak at once: a joke
/// is said only in a pause long enough for all of it, at its moment or after the line it would have cut into. One that
/// finds no such pause within <see cref="MaxJokeWaitMs"/> is left out rather than said over the narrator, unless the
/// clip was given room after its narration (<c>tail</c>): then it is said there, after the narrator's last line.
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

    /// <summary>The longest a joke waits for a pause in the narration before it is left out.</summary>
    internal const int MaxJokeWaitMs = 6000;

    public sealed record Result(List<RenderVisualEntry> Cues, int JokesMoved, int JokesDropped, int SoundsWaited, int SoundsLeftOut);

    /// <param name="speech">The narrator's lines, start and end in milliseconds; null when nobody narrates.</param>
    /// <param name="tail">The clip runs on past the narration to make room for the roast: a joke with no pause to fit goes there.</param>
    public static Result Arrange(IReadOnlyList<RenderVisualEntry> cues, long videoMs, IReadOnlyList<(long Start, long End)>? speech = null, bool tail = false)
    {
        static long Length(RenderVisualEntry c) => c.DurationMs > 0 ? c.DurationMs : UnknownMs;
        List<(long Start, long End)> lines = [.. (speech ?? []).Where(s => s.End > s.Start).OrderBy(s => s.Start)];
        int jokesMoved = 0, jokesDropped = 0, waited = 0, leftOut = 0;
        if (lines.Count > 0)
        {
            // Each joke first goes to the first pause, at or after its moment, that holds all of it; with room made after
            // the narration, one that finds none is said there instead. The walk below keeps two from overlapping.
            var placed = new List<RenderVisualEntry>();
            var afterNarration = lines.Max(l => l.End) + GapMs;
            foreach (var c in cues)
            {
                if (!c.Voice || Length(c) > SongMs)
                {
                    placed.Add(c);
                }
                else if ((Pause(c.TimestampMs, c.TimestampMs, Length(c), lines, videoMs)
                    ?? (tail && afterNarration + Length(c) <= videoMs ? (long?)afterNarration : null)) is not { } at)
                {
                    jokesDropped++;
                }
                else
                {
                    if (at != c.TimestampMs)
                        jokesMoved++;
                    placed.Add(c with { TimestampMs = at });
                }
            }
            cues = placed;
        }

        // Without a narrator, jokes alone keep the times the roast gave them; over one, the walk below also keeps two
        // jokes moved into the same pause from talking over each other.
        if (!cues.Any(c => c.Voice) || (!cues.Any(c => !c.Voice) && lines.Count == 0))
            return new([.. cues], jokesMoved, jokesDropped, 0, 0);

        var output = new List<RenderVisualEntry>();

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
                // A joke that waited behind a meme sound or another joke still has to fit a pause in the narration.
                var fit = lines.Count == 0 ? start : Pause(start, joke.TimestampMs, Length(joke), lines, videoMs);
                if (fit is not { } at || at + Length(joke) > videoMs)
                {
                    jokesDropped++;
                    continue;
                }

                start = at;
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
                    TimestampMs = start,
                    VisualEffect = null,
                    CaptionText = null,
                    CaptionPosition = null,
                    OverlayPath = null,
                });
                waited++;
            }

            free = start + Length(sound) + GapMs;
        }

        return new([.. output.OrderBy(c => c.TimestampMs)], jokesMoved, jokesDropped, waited, leftOut);
    }

    /// <summary>
    /// The first time, at or after <paramref name="from"/>, at which a voice of <paramref name="length"/> fits between
    /// the narrator's lines with a breath either side and ends before the video does; null when there is none within
    /// <see cref="MaxJokeWaitMs"/> of <paramref name="moment"/>.
    /// </summary>
    /// <param name="lines">The narrator's lines, in order of their start.</param>
    internal static long? Pause(long from, long moment, long length, IReadOnlyList<(long Start, long End)> lines, long videoMs)
    {
        var start = from;
        while (start - moment <= MaxJokeWaitMs && start + length <= videoMs)
        {
            var end = start + length;
            var clash = lines.Where(l => l.Start < end + GapMs && l.End + GapMs > start).Select(l => l.End).DefaultIfEmpty(-1).Max();
            if (clash < 0)
                return start;
            start = clash + GapMs;
        }

        return null;
    }

    private static bool HasVisuals(RenderVisualEntry cue) =>
        !string.IsNullOrWhiteSpace(cue.CaptionText) || !string.IsNullOrEmpty(cue.OverlayPath)
        || (cue.VisualEffect is { } effect && effect != "None");

    /// <summary>The cue's caption, sticker and effect at its moment, with no sound.</summary>
    private static RenderVisualEntry WithoutSound(RenderVisualEntry cue) => cue with { SoundPath = "", DurationMs = 0 };
}
