namespace PoRedoMedia.Api.Features.Video.TacticalAi;

/// <summary>
/// Goal-Oriented Action Planning (GOAP) comedy beat planner (&lt;0.5 ms).
/// Uses bitmask world states and A* search to plan Setup -&gt; Escalation -&gt; SilenceFeint -&gt; Payoff -&gt; Callback arcs
/// within the video's cue budget.
/// </summary>
public static class GoapComedyPlanner
{
    [Flags]
    public enum BeatState : ulong
    {
        None = 0,
        HasSetup = 1UL << 0,
        HasEscalation = 1UL << 1,
        HasSilenceFeint = 1UL << 2,
        HasPayoff = 1UL << 3,
        HasCallback = 1UL << 4,
    }

    public sealed record BeatAction(string Name, BeatState Preconditions, BeatState Effects, float Cost);

    private static readonly BeatAction[] DefaultActions =
    [
        new("EstablishSetup", BeatState.None, BeatState.HasSetup, 1.0f),
        new("EscalateVisuals", BeatState.HasSetup, BeatState.HasEscalation, 1.2f),
        new("DeadpanSilenceFeint", BeatState.HasEscalation, BeatState.HasSilenceFeint, 0.8f),
        new("DeliverPunchlinePayoff", BeatState.HasSetup | BeatState.HasEscalation, BeatState.HasPayoff, 1.5f),
        new("FeintedPayoffDrop", BeatState.HasSilenceFeint, BeatState.HasPayoff, 0.9f),
        new("CallbackStinger", BeatState.HasPayoff, BeatState.HasCallback, 1.1f),
    ];

    /// <summary>Plans the lowest-cost comedy beat sequence reaching <paramref name="goal"/> from <paramref name="initial"/>.</summary>
    public static IReadOnlyList<BeatAction> Plan(BeatState initial, BeatState goal, int maxSteps = 8)
    {
        var open = new PriorityQueue<(BeatState State, List<BeatAction> Path, float G), float>();
        var bestCost = new Dictionary<BeatState, float> { [initial] = 0f };
        open.Enqueue((initial, [], 0f), Heuristic(initial, goal));

        while (open.Count > 0)
        {
            var (state, path, g) = open.Dequeue();
            if ((state & goal) == goal)
                return path;
            if (path.Count >= maxSteps)
                continue;

            foreach (var action in DefaultActions)
            {
                if ((state & action.Preconditions) != action.Preconditions)
                    continue;
                var next = state | action.Effects;
                if (next == state)
                    continue;

                var nextG = g + action.Cost;
                if (bestCost.TryGetValue(next, out var seen) && seen <= nextG)
                    continue;

                bestCost[next] = nextG;
                var nextPath = new List<BeatAction>(path.Count + 1);
                nextPath.AddRange(path);
                nextPath.Add(action);
                open.Enqueue((next, nextPath, nextG), nextG + Heuristic(next, goal));
            }
        }

        return [];
    }

    private static float Heuristic(BeatState current, BeatState goal)
        => System.Numerics.BitOperations.PopCount((ulong)(goal & ~current));
}
