using System.Diagnostics.CodeAnalysis;
using Microsoft.JSInterop;

namespace PoRedoMedia.Client.Services;

/// <summary>One switch under Settings → Advanced.</summary>
public sealed record AppSetting(string Key, string Label, string Hint);

/// <summary>
/// The switches under Settings → Advanced. Every one starts off, so the app first shows its
/// simplest face; switching one on adds to the screen and takes nothing away. They live in this
/// browser's storage, and follow a signed-in account through <see cref="PreferenceSync"/>.
/// </summary>
public sealed class AppSettings
{
    private readonly IJSRuntime _js;
    private readonly PreferenceSync _sync;

    public AppSettings(IJSRuntime js, PreferenceSync sync)
    {
        _js = js;
        _sync = sync;
        // The account's switches replaced this browser's: whatever they show or hide updates.
        sync.Pulled += () => Changed?.Invoke();
    }

    public static readonly AppSetting OnDeviceAi = new("po.adv.device-ai", "On-device AI",
        "Describe pictures in this browser, for free. Downloads about 230 MB the first time.");

    public static readonly AppSetting Recipes = new("po.adv.recipes", "Recipes",
        "Save a set of ticked functions and use it again with one tap.");

    public static readonly AppSetting CostEstimates = new("po.adv.costs", "Cost estimates",
        "What each run is likely to cost in AI fees, and the total for this tab.");

    /// <summary>Kept under the key the effects layer reads (js/fx/fx-core.js), which also honours reduced motion.</summary>
    public static readonly AppSetting VisualEffects = new("po.fx", "Visual effects",
        "A moving backdrop, particles, the run orb, picture reveals, confetti and sparks, track waveforms and tilting cards.");

    public static readonly AppSetting[] Advanced = [OnDeviceAi, Recipes, CostEstimates, VisualEffects];

    /// <summary>Raised after a switch changes, so whatever it shows or hides can update.</summary>
    public event Action? Changed;

    // Read straight from the browser's storage: the WASM runtime calls JS in-process, so pages
    // can ask while they render instead of waiting a round trip.
    public bool this[AppSetting setting] => Read(setting.Key) == "on";

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The result is a string, which trimming always keeps.")]
    private string? Read(string key) => _js is IJSInProcessRuntime local ? local.Invoke<string?>("poMedia.read", key) : null;

    public async Task SetAsync(AppSetting setting, bool on)
    {
        if (setting == VisualEffects)
            await _js.InvokeVoidAsync("poFx.setEnabled", on);
        else
            await _js.InvokeVoidAsync("poMedia.write", setting.Key, on ? "on" : null);
        _sync.Schedule();
        Changed?.Invoke();
    }
}
