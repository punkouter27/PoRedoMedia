namespace PoRedoMedia.Api.Features.Video.TacticalAi;

/// <summary>
/// Dynamic Pacing / Intensity Director (DDA, &lt;0.05 ms).
/// Tracks a 32-slot intensity ring buffer and cycles BuildUp -&gt; Peak -&gt; SilenceFeint -&gt; Relax
/// so meme density breathes like a real comedy edit rather than firing uniformly.
/// </summary>
public sealed class DynamicPacingDirector
{
    public enum Phase { BuildUp, Peak, SilenceFeint, Relax }

    private readonly float[] _history = new float[32];
    private int _cursor;
    private int _count;

    public Phase CurrentPhase { get; private set; } = Phase.BuildUp;

    /// <summary>Average intensity across the sliding window [0, 1].</summary>
    public float RollingIntensity
    {
        get
        {
            if (_count == 0) return 0f;
            var sum = 0f;
            for (var i = 0; i < _count; i++) sum += _history[i];
            return sum / _count;
        }
    }

    /// <summary>Records a cue window's intensity and returns the recommended density multiplier for the next cue.</summary>
    public float ObserveAndModulate(float intensity)
    {
        _history[_cursor] = Math.Clamp(intensity, 0f, 1f);
        _cursor = (_cursor + 1) % _history.Length;
        if (_count < _history.Length) _count++;

        var avg = RollingIntensity;
        CurrentPhase = CurrentPhase switch
        {
            Phase.BuildUp when avg >= 0.65f => Phase.Peak,
            Phase.Peak when avg >= 0.78f => Phase.SilenceFeint,
            Phase.SilenceFeint => Phase.Relax,
            Phase.Relax when avg <= 0.35f => Phase.BuildUp,
            _ => CurrentPhase,
        };

        return CurrentPhase switch
        {
            Phase.BuildUp => 1.15f,
            Phase.Peak => 1.35f,
            Phase.SilenceFeint => 0.25f, // intentional deadpan pause before payoff
            Phase.Relax => 0.65f,
            _ => 1.0f,
        };
    }
}
