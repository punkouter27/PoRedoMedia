namespace PoRedoMedia.Api.Features.Video.TacticalAi;

/// <summary>
/// Comedic Silence-Feint Detector (&lt;0.3 ms) using O(N) prefix sums over the audio RMS envelope.
/// Detects sudden silence valleys immediately following loud hits — the prime moments for deadpan
/// captions, cricket sounds, or cutaway zooms.
/// </summary>
public static class SilenceFeintDetector
{
    public readonly record struct SilenceFeint(long TimestampMs, float DropDb);

    /// <summary>Finds deadpan silence valleys where energy drops sharply after a loud moment.</summary>
    public static IReadOnlyList<SilenceFeint> Detect(ReadOnlySpan<float> rms, double framesPerSecond, float minDropDb = 10f)
    {
        if (rms.Length < 12 || framesPerSecond <= 0)
            return [];

        var window = Math.Max(2, (int)Math.Round(framesPerSecond * 0.4));
        if (rms.Length < window * 2 + 2)
            return [];

        var prefix = new double[rms.Length + 1];
        for (var i = 0; i < rms.Length; i++)
            prefix[i + 1] = prefix[i] + Math.Max(1e-5, rms[i]);

        var results = new List<SilenceFeint>();
        var minGapFrames = Math.Max(window, (int)Math.Round(framesPerSecond * 2.0));
        var lastHit = -minGapFrames;

        for (var i = window; i <= rms.Length - window; i++)
        {
            if (i - lastHit < minGapFrames)
                continue;

            var before = (prefix[i] - prefix[i - window]) / window;
            var after = (prefix[i + window] - prefix[i]) / window;
            if (before < 0.04)
                continue;

            var dropDb = (float)(20.0 * Math.Log10(before / Math.Max(1e-5, after)));
            if (dropDb >= minDropDb)
            {
                results.Add(new SilenceFeint((long)Math.Round(i / framesPerSecond * 1000.0), dropDb));
                lastHit = i;
            }
        }

        return results;
    }
}
