using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Client.Services;

/// <summary>
/// Makes the Settings switches, the look-and-sound choices that follow a person (effects and
/// interface sounds) and the saved recipes follow the account. On sign-in the account's set is
/// read and written into this browser's storage (the account wins when it has any); after a change
/// the whole set is sent back, once things have been quiet for a moment. A signed-out visitor
/// keeps them in this browser only.
/// </summary>
public sealed class PreferenceSync(HttpClient http, IJSRuntime js) : IDisposable
{
    /// <summary>How long changes must settle before they are sent.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);

    private bool _signedIn;
    private CancellationTokenSource? _pending;

    /// <summary>Raised after the account's set replaced this browser's.</summary>
    public event Action? Pulled;

    /// <summary>The browser-storage keys that follow the account.</summary>
    public static bool IsSynced(string key) =>
        key.StartsWith("po.adv.", StringComparison.Ordinal) || key.StartsWith("po.sfx.", StringComparison.Ordinal)
        || key is "po.fx" || key == UiStore.Recipes;

    /// <summary>
    /// What to write into this browser: every key the account has, and null (remove) for each
    /// synced key this browser has and the account does not.
    /// </summary>
    public static Dictionary<string, string?> Changes(IReadOnlyDictionary<string, string> local, IReadOnlyDictionary<string, string> account)
    {
        var changes = account.Where(a => IsSynced(a.Key) && local.GetValueOrDefault(a.Key) != a.Value)
            .ToDictionary(a => a.Key, a => (string?)a.Value);
        foreach (var key in local.Keys.Where(k => IsSynced(k) && !account.ContainsKey(k)))
            changes[key] = null;
        return changes;
    }

    /// <summary>Called once the user is known to be signed in.</summary>
    public async Task PullAsync()
    {
        _signedIn = true;
        try
        {
            var account = (await http.GetFromJsonAsync("api/preferences", WireJson.Default.PreferencesDto))?.Values ?? [];
            if (account.Count == 0)
            {
                // A first sign-in: this browser's choices become the account's.
                await PushAsync();
                return;
            }

            var changes = Changes(await ReadLocalAsync(), account);
            if (changes.Count == 0)
                return;

            foreach (var (key, value) in changes)
                await js.InvokeVoidAsync("poMedia.write", key, value);
            if (changes.ContainsKey("po.fx"))
                await js.InvokeVoidAsync("poFx.setEnabled", account.GetValueOrDefault("po.fx") == "on");
            Pulled?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or JSException or TaskCanceledException)
        {
            // Offline or refused: this browser's own choices stand.
        }
    }

    /// <summary>Sends the whole set once changes stop for <see cref="Quiet"/>. Nothing when signed out.</summary>
    public void Schedule()
    {
        if (!_signedIn)
            return;

        _pending?.Cancel();
        _pending?.Dispose();
        _pending = new CancellationTokenSource();
        _ = PushLaterAsync(_pending.Token);
    }

    private async Task PushLaterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Quiet, ct);
            await PushAsync();
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or JSException)
        {
            // Superseded by a later change, or offline: the next change sends the set again.
        }
    }

    private async Task PushAsync()
    {
        var values = (await ReadLocalAsync()).Where(v => IsSynced(v.Key)).ToDictionary();
        using var response = await http.PutAsJsonAsync("api/preferences", new PreferencesDto(values), WireJson.Default.PreferencesDto);
    }

    private async Task<Dictionary<string, string>> ReadLocalAsync() =>
        JsonSerializer.Deserialize(await js.InvokeAsync<string>("poMedia.readAll"), WireJson.Default.DictionaryStringString) ?? [];

    public void Dispose()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }
}
