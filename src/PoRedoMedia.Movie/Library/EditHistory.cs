namespace PoRedoMedia.Movie.Library;

/// <summary>Undo and redo over whole values: each edit remembers what was there before it.</summary>
/// <param name="kept">How many steps back are remembered; older ones are dropped.</param>
/// <param name="clock">The time now; the system clock unless a test sets its own.</param>
public sealed class EditHistory<T>(int kept = 50, Func<DateTimeOffset>? clock = null)
{
    /// <summary>
    /// The longest pause inside one continuous change. A slider drag ticks many times a second; a caption change made
    /// after a longer pause is a step of its own, not more of the last drag.
    /// </summary>
    private static readonly TimeSpan MergeGap = TimeSpan.FromSeconds(1);

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly List<T> _undo = [];
    private readonly List<T> _redo = [];
    private DateTimeOffset? _lastMerge;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Records an edit that replaced <paramref name="before"/>. Nothing undone can be redone after it.</summary>
    /// <param name="merges">True for one tick of a continuous change, such as a slider drag: a run of them is undone as one.</param>
    public void Record(T before, bool merges = false)
    {
        var now = _now();
        if (!(merges && now - _lastMerge <= MergeGap))
        {
            _undo.Add(before);
            if (_undo.Count > kept) _undo.RemoveAt(0);
        }
        _lastMerge = merges ? now : null;
        _redo.Clear();
    }

    /// <summary>Steps back from <paramref name="current"/>; false when there is nothing to undo.</summary>
    public bool TryUndo(T current, out T previous) => Travel(_undo, _redo, current, out previous);

    public bool TryRedo(T current, out T next) => Travel(_redo, _undo, current, out next);

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _lastMerge = null;
    }

    private bool Travel(List<T> from, List<T> to, T current, out T target)
    {
        target = current;
        if (from.Count == 0) return false;
        target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(current);
        _lastMerge = null;
        return true;
    }
}
