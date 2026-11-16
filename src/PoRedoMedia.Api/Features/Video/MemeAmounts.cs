using PoRedoMedia.Api.Features.Render;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Video;

/// <summary>
/// Thins the directed meme cues to the amounts a <see cref="MemeMix"/> asks for: a share of the meme sounds, and a share of
/// the pictures (caption, sticker and effect). The cues kept are spread evenly over the clip. Pure.
/// </summary>
/// <remarks>
/// More than the usual pictures (levels 3 and 4) is asked of the director instead (<see cref="DirectorPrompt"/>), and
/// more than the usual sounds is planned up front.
/// </remarks>
internal static class MemeAmounts
{
    /// <summary>
    /// Share of the directed meme sounds kept, by <see cref="MemeMix.Sounds"/>. Lots keeps every one; Max keeps every one
    /// of a denser plan (<see cref="PlacementPlanner"/>).
    /// </summary>
    internal static readonly double[] SoundShare = [0, 0.3, 0.6, 1, 1];

    /// <summary>Share of the directed pictures kept, by <see cref="MemeMix.Pictures"/>; 2 and up keep every one.</summary>
    internal static readonly double[] PictureShare = [0, 0.5, 1, 1, 1];

    public static List<RenderVisualEntry> Apply(IReadOnlyList<RenderVisualEntry> cues, MemeMix mix)
    {
        var keepSound = Spread(cues.Count, SoundShare[mix.Sounds]);
        var keepPicture = Spread(cues.Count, PictureShare[mix.Pictures]);
        var kept = new List<RenderVisualEntry>();
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            if (!keepSound[i])
                cue = cue with { SoundPath = "", DurationMs = 0 };
            if (!keepPicture[i])
                cue = cue with { VisualEffect = null, CaptionText = null, CaptionPosition = null, OverlayPath = null, OverlayX = null, OverlayY = null, OverlayScale = null };
            if (cue.SoundPath.Length > 0 || HasPicture(cue))
                kept.Add(cue);
        }
        return kept;
    }

    /// <summary>The tag the sitcom laughs carry in the sound library.</summary>
    internal const string LaughTag = "laughtrack";

    /// <summary>
    /// Takes the effects off the cues the chaos level does not allow: none at all at 0, no deep-fry at 1. The director is
    /// asked the same, and this holds it to that.
    /// </summary>
    public static List<RenderVisualEntry> Calm(IReadOnlyList<RenderVisualEntry> cues, int chaos) => chaos switch
    {
        0 => [.. cues.Select(c => c with { VisualEffect = null }).Where(c => c.SoundPath.Length > 0 || HasPicture(c))],
        1 => [.. cues.Select(c => c.VisualEffect == nameof(PoRedoMedia.Shared.Enums.VisualEffectType.DeepFry) ? c with { VisualEffect = null } : c)
            .Where(c => c.SoundPath.Length > 0 || HasPicture(c))],
        _ => [.. cues],
    };

    /// <summary>
    /// A studio audience laughing after the jokes: after every other roast line when light, every one when heavy. With no
    /// roast, after the meme captions instead (every third, or every other). A laugh starts just after its joke ends,
    /// goes in only where it runs out before the next sound and the end of the clip, and plays as a meme sound (under
    /// a narrator). The laughs take turns through the ones given.
    /// </summary>
    public static List<RenderVisualEntry> AddLaughs(IReadOnlyList<RenderVisualEntry> cues, int level, IReadOnlyList<(string Path, int DurationMs)> laughs, long videoMs)
    {
        var result = cues.ToList();
        if (level <= 0 || laughs.Count == 0)
            return result;

        var jokes = cues.Where(c => c.Voice).OrderBy(c => c.TimestampMs).ToList();
        var after = jokes.Count > 0
            ? jokes.Where((_, i) => level >= 2 || i % 2 == 0).Select(c => c.TimestampMs + c.DurationMs).ToList()
            : cues.Where(c => !string.IsNullOrWhiteSpace(c.CaptionText)).OrderBy(c => c.TimestampMs)
                .Where((_, i) => i % (level >= 2 ? 2 : 3) == 0).Select(c => c.TimestampMs + Math.Max(c.DurationMs, 1_000)).ToList();

        var next = 0;
        foreach (var end in after)
        {
            var (path, length) = laughs[next % laughs.Count];
            var at = end + 150;
            var finish = at + length;
            // Never under another sound or roast line, and never past the end.
            if (finish > videoMs || result.Any(c => c.SoundPath.Length > 0 && c.TimestampMs < finish && c.TimestampMs + Math.Max(c.DurationMs, 500) > at))
                continue;
            result.Add(new RenderVisualEntry(at, path, null, null, null, DurationMs: length));
            next++;
        }
        return [.. result.OrderBy(c => c.TimestampMs)];
    }

    private static bool HasPicture(RenderVisualEntry cue)
        => !string.IsNullOrWhiteSpace(cue.CaptionText) || !string.IsNullOrEmpty(cue.OverlayPath)
            || (cue.VisualEffect is { } effect && effect != nameof(PoRedoMedia.Shared.Enums.VisualEffectType.None));

    /// <summary>Which of <paramref name="count"/> items to keep so that <paramref name="share"/> of them are, evenly spaced.</summary>
    internal static bool[] Spread(int count, double share)
    {
        var keep = new bool[count];
        var wanted = (int)Math.Round(count * share);
        for (var k = 0; k < wanted; k++)
            keep[(int)((long)k * count / wanted)] = true;
        return keep;
    }
}
