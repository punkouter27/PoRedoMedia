
namespace PoRedoMedia.Movie.Script;

/// <param name="Gap">Footage length minus narration length; positive means the narration ends early.</param>
public sealed record FitResult(string Dialogue, double Rate, TimeSpan Duration, TimeSpan Gap, bool WithinTolerance);

/// <summary>Speaks <paramref name="text"/> at <paramref name="rate"/> and returns how long the audio runs.</summary>
public delegate Task<TimeSpan> Speak(string text, double rate, CancellationToken ct);

/// <summary>Makes a clip's narration last as long as the user's footage, by rewriting it and then nudging the pace.</summary>
public static class Conformance
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MinFootage = TimeSpan.FromSeconds(5);
    /// <summary>
    /// The longest film a clip takes: a minute, as long as any clip may run, with the two seconds' slack the Library
    /// allows a film recorded to "one minute".
    /// </summary>
    public static readonly TimeSpan MaxFootage = TimeSpan.FromSeconds(62);
    public const int MaxRewrites = 3;

    public static async Task<FitResult> FitAsync(string dialogue, TimeSpan footage, IScriptWriter writer, Speak speak, CancellationToken ct)
    {
        if (footage < MinFootage || footage > MaxFootage)
            throw new ArgumentOutOfRangeException(nameof(footage), "Footage must be between 5 seconds and 1 minute long.");

        var best = (Text: dialogue, Duration: await speak(dialogue, 1.0, ct));
        var attempt = best;
        // Aim each request by how the last one actually came out, which also cancels a writer that runs long or short.
        var ask = (double)Durations.WordCount(dialogue);
        for (var i = 0; i < MaxRewrites && !Fits(best.Duration); i++)
        {
            // A recording with no length gives no measure to scale by: the ratio would be endless, and a request for
            // billions of words is what came of it. The longest film allowed caps the count however the last came out.
            if (attempt.Duration <= TimeSpan.Zero)
                throw new InvalidOperationException("The voice came back with no sound, so its words could not be fitted to the film. Try again in a moment.");
            ask = Math.Min(ask * (footage / attempt.Duration), Durations.WordsFor(MaxFootage));
            var text = await writer.RewriteToLengthAsync(attempt.Text, Math.Max(1, (int)Math.Round(ask)), ct);
            attempt = (text, await speak(text, 1.0, ct));
            if ((footage - attempt.Duration).Duration() < (footage - best.Duration).Duration())
                best = attempt;
        }
        if (Fits(best.Duration))
            return Result(best.Text, 1.0, best.Duration);

        var rate = Math.Clamp(best.Duration / footage, 1 - Ssml.MaxRateChange, 1 + Ssml.MaxRateChange);
        return Result(best.Text, rate, await speak(best.Text, rate, ct));

        bool Fits(TimeSpan duration) => (footage - duration).Duration() <= Tolerance;
        FitResult Result(string text, double r, TimeSpan duration) => new(text, r, duration, footage - duration, Fits(duration));
    }
}
