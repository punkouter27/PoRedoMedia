namespace PoRedoMedia.Movie.Pictures;

/// <summary>How good a picture is asked to be. Cost and time rise steeply with each step.</summary>
public enum Quality { Low, Medium, High }

/// <param name="ReferencePath">Picture whose character must be kept, normally the host's character sheet; null for none.</param>
/// <param name="Transparent">Nothing behind the subject, so the picture can be laid over another.</param>
public sealed record ImageRequest(string Prompt, string? ReferencePath, string Size = "1536x1024", Quality Quality = Quality.Medium, bool Transparent = false);

/// <summary>The picture service would not draw what was described. Another picture of the same idea may be drawn where this one was not.</summary>
/// <param name="flagged">What it objected to and how strongly, for example "violence: medium"; null when it did not say.</param>
/// <param name="said">The service's own words, when it gave any.</param>
public sealed class PictureDeclinedException(string? flagged, string? said = null)
    : InvalidOperationException($"The picture service would not draw this{(flagged is null ? "" : $" ({flagged})")}. {(said is null ? "" : said + " ")}Describe the picture another way, or choose another picture type for the clip.")
{
    public string? Flagged { get; } = flagged;
}

/// <summary>
/// What the picture models on offer can and cannot do. Only GPT-image takes a quality and draws on a see-through
/// background; FLUX.1 Kontext and Gemini do neither.
/// </summary>
public static class ImageModels
{
    public const string Standard = "gpt-image-1-mini";

    public const string Flux = "FLUX.1-Kontext-pro";

    public static bool IsFlux(string model) => model.Contains("flux", StringComparison.OrdinalIgnoreCase);

    /// <summary>The model can leave the background see-through, which a layered picture's front layer needs.</summary>
    public static bool IsGptImage(string model) => model.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase);

    public static bool HasTransparency(string model) => IsGptImage(model);

    /// <summary>The model draws better or worse for the asking, so a cheaper draft is worth having.</summary>
    public static bool HasQuality(string model) => IsGptImage(model);
}

/// <summary>Makes one picture and saves it as a PNG at <paramref name="outputPath"/>.</summary>
/// <exception cref="InvalidOperationException">The service refused or failed; the message is fit to show the user.</exception>
public delegate Task ImageMaker(ImageRequest request, string outputPath, CancellationToken ct);
