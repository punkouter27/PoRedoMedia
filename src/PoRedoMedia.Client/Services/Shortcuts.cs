namespace PoRedoMedia.Client.Services;

/// <summary>
/// Routes the keys the app's one keydown listener (Shared/ShortcutKeys.razor) sends. A page that
/// has keys of its own registers a handler for as long as it is open; the newest handler is asked
/// first, and a key it does not take falls through to the app-wide keys.
/// </summary>
public sealed class Shortcuts
{
    private readonly List<Func<string, Task<bool>>> _handlers = [];

    /// <summary>The keys every page knows, for the "?" dialog.</summary>
    public static readonly (string Key, string What)[] Help =
    [
        ("U", "Add media: choose a file on Create, open Add media in the Library"),
        ("R", "Run, on Create when the ticked functions are ready"),
        ("/", "Search the Library"),
        ("?", "Show these shortcuts"),
        ("← → ↑ ↓", "Library: move between items"),
        ("Enter", "Library: use the open item as the source of a run"),
        ("P", "Library: pin or unpin the open item"),
        ("C", "Library: compare the open picture with the one it was made from"),
        ("Del", "Library: delete the open item"),
        ("Esc", "Close the open item, or clear the selection"),
    ];

    /// <summary>Takes keys until the returned handle is disposed.</summary>
    public IDisposable Register(Func<string, Task<bool>> handler)
    {
        _handlers.Add(handler);
        return new Registration(() => _handlers.Remove(handler));
    }

    /// <summary>Offers the key to the newest page handler; false when none took it.</summary>
    public async Task<bool> OfferAsync(string key)
    {
        for (var i = _handlers.Count - 1; i >= 0; i--)
        {
            if (await _handlers[i](key))
                return true;
        }

        return false;
    }

    private sealed class Registration(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose()
        {
            _release?.Invoke();
            _release = null;
        }
    }
}
