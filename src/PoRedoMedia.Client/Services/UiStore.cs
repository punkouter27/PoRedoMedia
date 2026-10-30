using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.JSInterop;

namespace PoRedoMedia.Client.Services;

/// <summary>
/// Small things remembered in this browser: the last options used, saved recipes. Nothing here
/// is needed for the app to work, so a browser that refuses storage just forgets. Recipes follow a
/// signed-in account through <see cref="PreferenceSync"/>; the last options stay with the browser.
/// </summary>
public sealed class UiStore(IJSRuntime js, PreferenceSync sync)
{
    public const string LastOptions = "po.options";
    public const string Recipes = "po.recipes";

    public async Task<T?> ReadAsync<T>(string key, JsonTypeInfo<T> type)
    {
        try
        {
            return await js.InvokeAsync<string?>("poMedia.read", key) is { Length: > 0 } stored
                ? JsonSerializer.Deserialize(stored, type)
                : default;
        }
        catch (Exception ex) when (ex is JsonException or JSException)
        {
            return default;
        }
    }

    /// <summary>The stored text as it is, or null; for readers that parse it themselves.</summary>
    public async Task<string?> ReadRawAsync(string key)
    {
        try
        {
            return await js.InvokeAsync<string?>("poMedia.read", key);
        }
        catch (JSException)
        {
            return null;
        }
    }

    public async ValueTask WriteAsync<T>(string key, T value, JsonTypeInfo<T> type)
    {
        await js.InvokeVoidAsync("poMedia.write", key, JsonSerializer.Serialize(value, type));
        if (PreferenceSync.IsSynced(key))
            sync.Schedule();
    }
}
