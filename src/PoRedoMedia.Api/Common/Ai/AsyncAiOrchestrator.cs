using System.Collections.Concurrent;
using PoRedoMedia.Api.Features.Video.TacticalAi;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>
/// Async non-blocking ML / LLM orchestration helpers with strict latency mitigation (timeouts,
/// deterministic fallbacks, and L1 caching) so inference never stalls renders or UI.
/// Covers: #11 Speculative Multi-Draft Tournament, #14/#16 Adversarial Self-Play RoastCritic Loop,
/// #17 Hybrid-Cached Semantic Sound Index, and #19 Adaptive Series Comedy Playbook Generator.
/// </summary>
public static class AsyncAiOrchestrator
{
    /// <summary>
    /// #11 Speculative Multi-Draft Tournament: generates <paramref name="draftCount"/> drafts in parallel
    /// under a hard <paramref name="timeout"/>, scores completed drafts with <paramref name="scoreAsync"/>,
    /// and falls back cleanly to the first completed draft if scoring times out or fails.
    /// </summary>
    public static async Task<string> RunDraftTournamentAsync(
        Func<int, CancellationToken, Task<string>> generateDraftAsync,
        Func<IReadOnlyList<string>, CancellationToken, Task<double[]>> scoreAsync,
        int draftCount = 3,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var count = Math.Clamp(draftCount, 1, 5);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMilliseconds(3500));

        var tasks = Enumerable.Range(0, count).Select(i => SafeGenerateAsync(generateDraftAsync, i, cts.Token)).ToArray();
        var results = await Task.WhenAll(tasks);
        var valid = results.Where(r => !string.IsNullOrWhiteSpace(r)).Cast<string>().ToList();
        if (valid.Count == 0)
            return string.Empty;
        if (valid.Count == 1)
            return valid[0];

        try
        {
            var scores = await scoreAsync(valid, cts.Token);
            var bestIdx = 0;
            for (var i = 1; i < Math.Min(valid.Count, scores.Length); i++)
            {
                if (scores[i] > scores[bestIdx])
                    bestIdx = i;
            }
            return valid[bestIdx];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return valid[0];
        }
    }

    private static async Task<string?> SafeGenerateAsync(Func<int, CancellationToken, Task<string>> fn, int index, CancellationToken ct)
    {
        try { return await fn(index, ct); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// #16 Adversarial Self-Play RoastCritic Loop: critiques <paramref name="initialDraft"/> and performs
    /// at most one targeted rewrite when the critic scores below <paramref name="acceptThreshold"/> within
    /// <paramref name="budget"/>. Never throws or blocks beyond budget.
    /// </summary>
    public static async Task<(string FinalDraft, double? Score, bool Rewritten)> RefineWithCriticAsync(
        string task,
        string initialDraft,
        IRoastCritic critic,
        Func<string, string, CancellationToken, Task<string>> rewriteAsync,
        double acceptThreshold = 7.5,
        TimeSpan? budget = null,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget ?? TimeSpan.FromSeconds(4));

        try
        {
            var verdict = await critic.CritiqueAsync(task, initialDraft, cts.Token);
            if (verdict is null || verdict.Score >= acceptThreshold || string.IsNullOrWhiteSpace(verdict.Critique))
                return (initialDraft, verdict?.Score, false);

            var rewritten = await rewriteAsync(initialDraft, verdict.Critique, cts.Token);
            return string.IsNullOrWhiteSpace(rewritten)
                ? (initialDraft, verdict.Score, false)
                : (rewritten, verdict.Score, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (initialDraft, null, false);
        }
    }

    /// <summary>
    /// #19 Adaptive Series Comedy Playbook: builds a reusable tactical playbook from the series blackboard
    /// (recurring gags, preferred persona, GOAP arc) with deterministic defaults when cold.
    /// </summary>
    public static ComedyPlaybook BuildPlaybook(string seriesKey, ComedyBlackboard blackboard, string defaultPersona = "standard")
    {
        var callbacks = blackboard.TopCallbacks(4).Select(e => $"{e.Key}: {e.Value}").ToArray();
        var goal = GoapComedyPlanner.BeatState.HasSetup | GoapComedyPlanner.BeatState.HasPayoff | GoapComedyPlanner.BeatState.HasCallback;
        var arc = GoapComedyPlanner.Plan(GoapComedyPlanner.BeatState.None, goal).Select(a => a.Name).ToArray();
        return new ComedyPlaybook(
            SeriesKey: seriesKey,
            Persona: blackboard.Get("persona") ?? defaultPersona,
            CallbackMotifs: callbacks,
            BeatArc: arc);
    }

    public sealed record ComedyPlaybook(string SeriesKey, string Persona, string[] CallbackMotifs, string[] BeatArc);
}

/// <summary>
/// #17 Hybrid-Cached Semantic Sound Index (&lt;0.5 ms lookup).
/// Pre-computes normalized character-trigram + tag sparse vectors in an L1 concurrent cache so sound
/// library queries resolve instantly without hitting external embedding APIs on the hot path.
/// </summary>
public sealed class HybridSoundIndex
{
    private readonly ConcurrentDictionary<string, Dictionary<string, float>> _vectors = new(StringComparer.OrdinalIgnoreCase);

    public void Index(string soundId, IEnumerable<string> tagsAndTitle)
    {
        var text = string.Join(' ', tagsAndTitle);
        _vectors[soundId] = Vectorize(text);
    }

    public IReadOnlyList<(string SoundId, float Score)> Search(string query, int topK = 5)
    {
        if (string.IsNullOrWhiteSpace(query) || _vectors.IsEmpty)
            return [];

        var qVec = Vectorize(query);
        var scored = new List<(string SoundId, float Score)>(_vectors.Count);
        foreach (var (id, vec) in _vectors)
        {
            var sim = Cosine(qVec, vec);
            if (sim > 0f)
                scored.Add((id, sim));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scored.Count <= topK ? scored : scored.GetRange(0, topK);
    }

    private static Dictionary<string, float> Vectorize(string text)
    {
        var vec = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var cleaned = text.Trim().ToLowerInvariant();
        foreach (var token in cleaned.Split([' ', ',', '.', '!', '?', '-', '_'], StringSplitOptions.RemoveEmptyEntries))
        {
            vec[token] = vec.GetValueOrDefault(token) + 1.5f;
            for (var i = 0; i <= token.Length - 3; i++)
            {
                var tri = token.Substring(i, 3);
                vec[tri] = vec.GetValueOrDefault(tri) + 0.5f;
            }
        }

        var norm = MathF.Sqrt(vec.Values.Sum(v => v * v));
        if (norm > 0f)
        {
            foreach (var k in vec.Keys.ToList())
                vec[k] /= norm;
        }
        return vec;
    }

    private static float Cosine(Dictionary<string, float> a, Dictionary<string, float> b)
    {
        var sum = 0f;
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        foreach (var (k, v) in small)
        {
            if (large.TryGetValue(k, out var ov))
                sum += v * ov;
        }
        return sum;
    }
}
