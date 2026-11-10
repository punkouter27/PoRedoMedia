
namespace PoRedoMedia.Movie.Pictures;

public static class VisualMix
{
    /// <summary>Largest-remainder split of <paramref name="clipCount"/> clips; ties go to the earlier kind.</summary>
    public static IReadOnlyDictionary<VisualKind, int> Counts(MixPercentages mix, int clipCount)
    {
        (VisualKind Kind, int Percent)[] shares = [(VisualKind.Still, mix.Still), (VisualKind.MultiPanel, mix.MultiPanel), (VisualKind.TitleCard, mix.TitleCard)];
        if (shares.Any(s => s.Percent < 0) || shares.Sum(s => s.Percent) != 100)
            throw new ArgumentException("Percentages must be zero or more and add up to 100.", nameof(mix));

        var counts = shares.ToDictionary(s => s.Kind, s => s.Percent * clipCount / 100);
        var spare = clipCount - counts.Values.Sum();
        foreach (var (kind, _) in shares.OrderByDescending(s => s.Percent * clipCount % 100).Take(spare)) // stable sort keeps listed order on ties
            counts[kind]++;
        return counts;
    }

    /// <summary>
    /// Takes the script writer's choice of kind for each clip, within what can be made and paid for: a kind that cannot
    /// be made here, and AI video beyond <paramref name="maxAiVideos"/> clips, becomes a still. A script that chose
    /// nothing is dealt the default mix.
    /// </summary>
    public static Episode Settle(Episode episode, Func<VisualKind, bool> canMake, int maxAiVideos = 2)
    {
        if (episode.Clips.All(c => c.Visual.Kind == VisualKind.TitleCard))
            return ShortCards(Assign(episode, MixPercentages.Default), canMake);
        var videos = 0;
        bool Allowed(VisualKind kind) =>
            kind is VisualKind.TitleCard or VisualKind.UserVideo || (canMake(kind) && (kind != VisualKind.AiVideo || ++videos <= maxAiVideos));
        return ShortCards(episode with { Clips = [.. episode.Clips.Select(c => Allowed(c.Visual.Kind) ? c : WithKind(c, VisualKind.Still))] }, canMake);
    }

    /// <summary>The longest a title card is shown: a chapter break, not something to sit and look at.</summary>
    public static readonly TimeSpan LongestTitleCard = TimeSpan.FromSeconds(8);

    /// <summary>
    /// A title card whose words run longer than <see cref="LongestTitleCard"/> becomes moving text of its title where
    /// that can be drawn, and otherwise a picture, so nobody looks at one still card for half a minute. A clip the
    /// script writer would not write, and one picked by hand, keep theirs.
    /// </summary>
    private static Episode ShortCards(Episode episode, Func<VisualKind, bool> canMake) => episode with
    {
        Clips = [.. episode.Clips.Select(c => c.Visual is { Kind: VisualKind.TitleCard, KindLocked: false } && c.Active.Dialogue != ChatScriptWriter.LeftToWrite
            && Durations.Estimate(c.Active.Dialogue) > LongestTitleCard
            ? WithKind(c, canMake(VisualKind.KineticText) ? VisualKind.KineticText : VisualKind.Still)
            : c)],
    };

    /// <summary>A clip that changes kind starts clean: its old picture belongs to the kind it had.</summary>
    private static Clip WithKind(Clip clip, VisualKind kind) =>
        clip.Visual.Kind == kind ? clip : clip with { Visual = new VisualSpec(kind) };

    /// <summary>
    /// Deals stills, panel sequences and title cards among the clips that are one of those three, using
    /// <see cref="Episode.MixSeed"/>. Everything else is untouched: hand-picked kinds, the user's footage, and the
    /// animations, charts, photographs and footage the script chose, which a re-deal would otherwise throw away.
    /// </summary>
    public static Episode Assign(Episode episode, MixPercentages mix)
    {
        static bool Eligible(Clip c) => c.Visual is { KindLocked: false, Kind: VisualKind.Still or VisualKind.MultiPanel or VisualKind.TitleCard };

        var deck = Counts(mix, episode.Clips.Count(Eligible))
            .SelectMany(c => Enumerable.Repeat(c.Key, c.Value))
            .ToArray();
        new Random(episode.MixSeed).Shuffle(deck);

        var next = 0;
        return episode with
        {
            Clips = [.. episode.Clips.Select(c => Eligible(c) ? WithKind(c, deck[next++]) : c)],
        };
    }
}
