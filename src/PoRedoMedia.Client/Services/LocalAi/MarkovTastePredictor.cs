namespace PoRedoMedia.Client.LocalAi;

/// <summary>
/// 2nd-order Markov Chain user-pattern predictor (&lt;0.05 ms, pure C# WASM).
/// Learns transitions across chosen workflows (e.g. MemeCaption -&gt; RapRoast -&gt; Memeify) so the UI can
/// speculatively pre-warm local models or suggest the next action before the user clicks.
/// </summary>
public sealed class MarkovTastePredictor
{
    private readonly Dictionary<(string Prev2, string Prev1), Dictionary<string, int>> _order2 = new();
    private readonly Dictionary<string, Dictionary<string, int>> _order1 = new(StringComparer.OrdinalIgnoreCase);
    private string? _last2;
    private string? _last1;

    /// <summary>Records an observed user action key (e.g. function or persona name).</summary>
    public void Observe(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return;

        if (_last1 is not null)
        {
            Increment(_order1, _last1, action);
            if (_last2 is not null)
            {
                var key = (_last2, _last1);
                if (!_order2.TryGetValue(key, out var next))
                    _order2[key] = next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                next[action] = next.GetValueOrDefault(action) + 1;
            }
        }

        _last2 = _last1;
        _last1 = action;
    }

    /// <summary>Predicts the most likely next action with its empirical probability [0, 1].</summary>
    public (string? Action, float Confidence) PredictNext()
    {
        if (_last1 is null)
            return (null, 0f);

        if (_last2 is not null && _order2.TryGetValue((_last2, _last1), out var counts2) && counts2.Count > 0)
            return TopOf(counts2);

        if (_order1.TryGetValue(_last1, out var counts1) && counts1.Count > 0)
            return TopOf(counts1);

        return (null, 0f);
    }

    private static void Increment(Dictionary<string, Dictionary<string, int>> map, string from, string to)
    {
        if (!map.TryGetValue(from, out var counts))
            map[from] = counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        counts[to] = counts.GetValueOrDefault(to) + 1;
    }

    private static (string Action, float Confidence) TopOf(Dictionary<string, int> counts)
    {
        var total = 0;
        var bestKey = string.Empty;
        var bestCount = -1;
        foreach (var (k, v) in counts)
        {
            total += v;
            if (v > bestCount)
            {
                bestCount = v;
                bestKey = k;
            }
        }
        return (bestKey, total > 0 ? bestCount / (float)total : 0f);
    }
}
