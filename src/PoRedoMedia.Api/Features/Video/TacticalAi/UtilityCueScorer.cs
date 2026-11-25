namespace PoRedoMedia.Api.Features.Video.TacticalAi;

/// <summary>
/// Utility AI scorer for meme cue candidates (&lt;0.1 ms, zero heap allocation).
/// Evaluates normalized response curves (energy, tag relevance, cooldown gap, motion spike, novelty)
/// and combines them with geometric-mean compensation so a veto (0 utility) cleanly suppresses a cue.
/// </summary>
public static class UtilityCueScorer
{
    public readonly record struct Considerations(
        float AudioEnergy,
        float TagRelevance,
        float SecondsSinceLastCue,
        float MotionSpike,
        float Novelty,
        bool OverlapsNarrator);

    /// <summary>Scores a candidate cue in [0, 1] using non-linear response curves.</summary>
    public static float Score(in Considerations c)
    {
        if (c.OverlapsNarrator)
            return 0f;

        // Logistic curve for audio energy (steep rise around 0.5).
        var energy = Logistic(c.AudioEnergy, k: 8f, midpoint: 0.45f);
        // Quadratic curve rewarding strong tag matches.
        var relevance = Math.Clamp(c.TagRelevance, 0f, 1f);
        relevance = 0.25f + 0.75f * (relevance * relevance);
        // Saturating cooldown curve: ramps smoothly up to 4 seconds since last cue.
        var spacing = 1f - MathF.Exp(-MathF.Max(0f, c.SecondsSinceLastCue) / 2.2f);
        // Motion spike boost (floor at 0.35 so quiet reaction cues still score).
        var motion = 0.35f + 0.65f * Math.Clamp(c.MotionSpike, 0f, 1f);
        // Novelty curve penalizing recently repeated sounds/effects.
        var novelty = 0.2f + 0.8f * Math.Clamp(c.Novelty, 0f, 1f);

        // Geometric mean with compensation factor (Dave Mark's Utility AI formula).
        var raw = energy * relevance * spacing * motion * novelty;
        if (raw <= 0f)
            return 0f;

        const int count = 5;
        var modFactor = 1f - (1f / count);
        var makeup = (1f - raw) * modFactor;
        return Math.Clamp(raw + (makeup * raw), 0f, 1f);
    }

    private static float Logistic(float x, float k, float midpoint)
        => 1f / (1f + MathF.Exp(-k * (Math.Clamp(x, 0f, 1f) - midpoint)));
}
