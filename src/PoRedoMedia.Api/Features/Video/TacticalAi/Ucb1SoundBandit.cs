namespace PoRedoMedia.Api.Features.Video.TacticalAi;

/// <summary>
/// Deterministic UCB1 Multi-Armed Bandit (&lt;0.1 ms) for anti-repetition sound &amp; sticker selection.
/// Balances tag relevance (exploitation) with under-used catalog variety (exploration) so repeated
/// runs stay fresh without sacrificing comedy fit.
/// </summary>
public sealed class Ucb1SoundBandit(float explorationC = 0.35f)
{
    private readonly Dictionary<string, (int Picks, float TotalReward)> _stats = new(StringComparer.OrdinalIgnoreCase);
    private int _totalPicks;

    /// <summary>Selects the candidate ID with the highest UCB1 score combining base relevance and exploration.</summary>
    public string Select(IReadOnlyList<(string Id, float BaseRelevance)> candidates)
    {
        if (candidates.Count == 0)
            throw new ArgumentException("At least one candidate is required.", nameof(candidates));

        var bestId = candidates[0].Id;
        var bestScore = float.NegativeInfinity;
        var logN = MathF.Log(Math.Max(2, _totalPicks + 1));

        for (var i = 0; i < candidates.Count; i++)
        {
            var (id, relevance) = candidates[i];
            float score;
            if (!_stats.TryGetValue(id, out var st) || st.Picks == 0)
            {
                // Unpicked items get a clean exploration bonus scaled by their relevance.
                score = relevance + explorationC;
            }
            else
            {
                var meanReward = st.TotalReward / st.Picks;
                var bonus = explorationC * MathF.Sqrt(logN / st.Picks);
                // Penalize heavy repetition inside the same session.
                var repeatPenalty = 0.12f * Math.Min(4, st.Picks);
                score = (relevance * 0.65f) + (meanReward * 0.35f) + bonus - repeatPenalty;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestId = id;
            }
        }

        RecordPick(bestId, reward: 0.7f);
        return bestId;
    }

    public void RecordPick(string id, float reward)
    {
        _totalPicks++;
        _stats.TryGetValue(id, out var st);
        _stats[id] = (st.Picks + 1, st.TotalReward + Math.Clamp(reward, 0f, 1f));
    }
}
