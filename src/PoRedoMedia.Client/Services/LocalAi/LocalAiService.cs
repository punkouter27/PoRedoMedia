using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Client.LocalAi;

/// <summary>
/// Entry point the UI uses for browser-native inference.
/// </summary>
/// <remarks>
/// Probes the device once per page load and caches it — <c>requestAdapter()</c> is not free, and
/// the answer cannot change without a reload.
/// </remarks>
public sealed class LocalAiService(IJSRuntime js, ILoggerFactory loggerFactory) : IAsyncDisposable
{
    // A valid 1x1 PNG: enough for the model to load and run once.
    private static readonly byte[] TinyPicture = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private JsLocalInferenceRuntime? _runtime;
    private DeviceCapabilities? _capabilities;
    private bool _visionReady;

    /// <summary>
    /// True when the vision model can describe a picture without a download: it already ran in this
    /// tab, or its weights are in this browser's cache.
    /// </summary>
    public async ValueTask<bool> IsVisionReadyAsync()
    {
        if (_visionReady)
            return true;

        try
        {
            return _visionReady = await js.InvokeAsync<bool>("poLocalAiIsCached", LocalModelRegistry.Vision.RepoId);
        }
        catch (JSException)
        {
            return false;
        }
    }

    /// <summary>Downloads the vision model and runs it once on a tiny picture, so later runs start straight away.</summary>
    public Task WarmUpAsync(IProgress<LocalInferenceStatus>? progress = null, CancellationToken ct = default) =>
        DescribeImageAsync(TinyPicture, prompt: null, progress, ct);

    /// <summary>
    /// Device capabilities, probed on first use. Returns <see cref="DeviceCapabilities.None"/> if
    /// the probe fails, so a JS error degrades to wasm rather than breaking the feature.
    /// </summary>
    public async ValueTask<DeviceCapabilities> GetCapabilitiesAsync()
    {
        if (_capabilities is not null) return _capabilities;

        try
        {
            var probe = await js.InvokeAsync<DeviceProbe>("poLocalAiProbeDevice");
            _capabilities = new DeviceCapabilities(
                probe.HasWebGpu, probe.HasShaderF16, probe.MaxBufferBytes, probe.AdapterDescription);
        }
        catch (JSException)
        {
            _capabilities = DeviceCapabilities.None;
        }

        return _capabilities;
    }

    /// <summary>Describes an image with the local vision model.</summary>
    public async Task<LocalInferenceOutcome> DescribeImageAsync(
        byte[] image,
        string? prompt = null,
        IProgress<LocalInferenceStatus>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new LocalInferenceStatus(LocalStage.Probing));
        var capabilities = await GetCapabilitiesAsync();

        _runtime ??= new JsLocalInferenceRuntime(js);
        var session = new LocalInferenceSession(_runtime, loggerFactory.CreateLogger("LocalAi"));
        var outcome = await session.RunAsync(LocalModelRegistry.Vision, capabilities, prompt ?? string.Empty, image, progress, ct);
        _visionReady = true;
        return outcome;
    }

    /// <summary>
    /// Detects normalized object/face bounding boxes using Florence-2 (<c>&lt;OD&gt;</c> or
    /// <c>&lt;CAPTION_TO_PHRASE_GROUNDING&gt;</c>) so sticker overlays can aim at real subjects.
    /// </summary>
    public async Task<IReadOnlyList<VisionBoxDto>> DetectObjectsAsync(
        byte[] image,
        string? phrase = null,
        IProgress<LocalInferenceStatus>? progress = null,
        CancellationToken ct = default)
    {
        var task = string.IsNullOrWhiteSpace(phrase) ? "<OD>" : $"<CAPTION_TO_PHRASE_GROUNDING>{phrase.Trim()}";
        try
        {
            var outcome = await DescribeImageAsync(image, task, progress, ct);
            if (string.IsNullOrWhiteSpace(outcome.Text))
                return [];
            return JsonSerializer.Deserialize(outcome.Text, WireJson.Default.VisionBoxDtoArray) ?? [];
        }
        catch (Exception ex) when (ex is LocalInferenceException or JsonException or JSException)
        {
            return [];
        }
    }

    /// <summary>
    /// Runs the compact on-device comedy director LLM (Qwen2.5-0.5B-Instruct) for instant zero-cost
    /// meme caption drafts, persona routing, and comic punch-up lines.
    /// </summary>
    public async Task<LocalDirectorDraftDto?> DraftComedyAsync(string prompt, CancellationToken ct = default)
    {
        var capabilities = await GetCapabilitiesAsync();
        var device = capabilities.HasWebGpu ? "WebGpu" : "Wasm";
        var dtype = LocalModelRegistry.TransformersDtype(DtypeVariant.Q4);
        try
        {
            var json = await js.InvokeAsync<string>(
                "poLocalAiDraft", ct, LocalModelRegistry.DirectorLlm.RepoId, dtype, device, prompt);
            return JsonSerializer.Deserialize(json, WireJson.Default.LocalDirectorDraftDto);
        }
        catch (Exception ex) when (ex is JSException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_runtime is not null) await _runtime.DisposeAsync();
        _runtime = null;
    }

    /// <summary>Wire shape of the JS device probe.</summary>
    private sealed record DeviceProbe(
        bool HasWebGpu, bool HasShaderF16, long? MaxBufferBytes, string? AdapterDescription);
}
