using System.Diagnostics.CodeAnalysis;
using Microsoft.JSInterop;

namespace PoRedoMedia.Client.Services;

/// <summary>One of the app's fixed switches, and whether it is on.</summary>
public sealed record AppSetting(string Key, bool On);

/// <summary>
/// What the app shows, fixed by the owner (2026-11-20): there is no Settings page. Cost estimates,
/// visual effects and the movie's advanced controls are on; on-device AI and recipes are off (and
/// interface sounds stay muted, js/sfx.js). A switch that is off can still be turned on by its key in
/// this browser's storage, which is how the browser tests reach those screens.
/// </summary>
public sealed class AppSettings(IJSRuntime js)
{
    public static readonly AppSetting OnDeviceAi = new("poredomedia:adv:device-ai", On: false);

    public static readonly AppSetting Recipes = new("poredomedia:adv:recipes", On: false);

    public static readonly AppSetting CostEstimates = new("poredomedia:adv:costs", On: true);

    /// <summary>Read by the effects layer itself (js/fx/fx-core.js), which also honours reduced motion.</summary>
    public static readonly AppSetting VisualEffects = new("poredomedia:fx", On: true);

    public static readonly AppSetting MovieAdvanced = new("poredomedia:adv:movie", On: true);

    // Read straight from the browser's storage: the WASM runtime calls JS in-process, so pages
    // can ask while they render instead of waiting a round trip.
    public bool this[AppSetting setting] => setting.On || Read(setting.Key) == "on";

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The result is a string, which trimming always keeps.")]
    private string? Read(string key) => js is IJSInProcessRuntime local ? local.Invoke<string?>("poMedia.read", key) : null;
}
