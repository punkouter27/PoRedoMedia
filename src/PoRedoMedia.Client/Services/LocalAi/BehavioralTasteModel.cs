using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Client.LocalAi;

/// <summary>
/// Lightweight online behavioral-cloning taste model (&lt;0.5 ms, pure C# WASM).
/// Learns from user feedback signals (Pin / Again = positive, Delete / Retry = negative) using
/// exponential moving-average gradient steps to recommend personalized <see cref="MemeMix"/> levels.
/// </summary>
public sealed class BehavioralTasteModel
{
    private float _roastBias = 1.5f;
    private float _memeBias = 2.0f;
    private float _chaosBias = 1.5f;
    private int _observations;

    public int Observations => _observations;

    /// <summary>Updates weights from a user interaction on a generated output.</summary>
    public void RecordFeedback(MemeMix usedMix, float reward)
    {
        var r = Math.Clamp(reward, -1f, 1f);
        const float lr = 0.25f;
        _roastBias = Math.Clamp(_roastBias + lr * r * (usedMix.RoastLevel - _roastBias), 0f, 3f);
        _memeBias = Math.Clamp(_memeBias + lr * r * (usedMix.MemeLevel - _memeBias), 0f, 3f);
        _chaosBias = Math.Clamp(_chaosBias + lr * r * (usedMix.Chaos - _chaosBias), 0f, 3f);
        _observations++;
    }

    /// <summary>Recommends a personalized <see cref="MemeMix"/>; falls back to <paramref name="baseline"/> when cold.</summary>
    public MemeMix Recommend(MemeMix baseline)
    {
        if (_observations < 2)
            return baseline;

        return baseline
            .WithRoast((int)MathF.Round(_roastBias))
            .WithMemes((int)MathF.Round(_memeBias)) with
        {
            Chaos = (int)MathF.Round(_chaosBias),
        };
    }
}
