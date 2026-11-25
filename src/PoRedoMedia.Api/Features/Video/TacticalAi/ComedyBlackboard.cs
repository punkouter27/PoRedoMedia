namespace PoRedoMedia.Api.Features.Video.TacticalAi;

/// <summary>
/// Hierarchical Blackboard Memory (Clip -&gt; Episode -&gt; Series) with salience decay (&lt;0.1 ms).
/// Stores recurring gags, roast targets, and used motifs so multi-clip movies and follow-up runs
/// can land callbacks without repeating stale jokes.
/// </summary>
public sealed class ComedyBlackboard(ComedyBlackboard? parent = null)
{
    public sealed record Entry(string Key, string Value, float Salience, int RemainingTtl);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string key, string value, float salience = 1.0f, int ttl = 5)
        => _entries[key] = new Entry(key, value, Math.Clamp(salience, 0.05f, 1.0f), Math.Max(1, ttl));

    public string? Get(string key)
        => _entries.TryGetValue(key, out var entry) ? entry.Value : parent?.Get(key);

    /// <summary>Decays salience and expires stale entries after each clip or turn.</summary>
    public void Decay(float factor = 0.8f)
    {
        List<string>? expired = null;
        foreach (var (k, e) in _entries)
        {
            var nextTtl = e.RemainingTtl - 1;
            var nextSalience = e.Salience * factor;
            if (nextTtl <= 0 || nextSalience < 0.1f)
                (expired ??= []).Add(k);
            else
                _entries[k] = e with { Salience = nextSalience, RemainingTtl = nextTtl };
        }
        if (expired is not null)
        {
            foreach (var k in expired)
                _entries.Remove(k);
        }
    }

    /// <summary>Returns the top-K most salient callback facts across this scope and parent scopes.</summary>
    public IReadOnlyList<Entry> TopCallbacks(int max = 5)
    {
        var merged = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (parent is not null)
        {
            foreach (var e in parent.TopCallbacks(max * 2))
                merged[e.Key] = e with { Salience = e.Salience * 0.85f };
        }
        foreach (var e in _entries.Values)
            merged[e.Key] = e;
        return [.. merged.Values.OrderByDescending(e => e.Salience).Take(max)];
    }
}
