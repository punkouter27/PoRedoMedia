using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace PoRedoMedia.Client.LocalAi;

/// <summary>
/// Hands a picture's bytes straight to the local-AI script (js/local-ai/local-ai-interop.js).
/// </summary>
/// <remarks>
/// A <c>[JSImport]</c> call passes the bytes as a view over .NET's own memory, so they cross as one
/// copy instead of a base64 string a third larger that both sides have to encode and decode, and
/// that went through the JSON interop besides. The view is only valid during the call, which is
/// why the script copies it and keeps it for the run that starts next.
/// </remarks>
[SupportedOSPlatform("browser")]
internal static partial class LocalAiBytes
{
    [JSImport("globalThis.poLocalAiStage")]
    internal static partial void Stage(string runId, [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes);
}
