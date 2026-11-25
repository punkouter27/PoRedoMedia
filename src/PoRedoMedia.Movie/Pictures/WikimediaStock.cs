using System.Text.Json;

namespace PoRedoMedia.Movie.Pictures;

/// <summary>
/// Public-domain and Creative Commons photographs from Wikimedia Commons. Requires no API key,
/// giving Create Movie a free stock fallback even when Pexels is not configured.
/// </summary>
public sealed class WikimediaStock(HttpClient http)
{
    private const string UserAgent = "PoRedoMedia/1.0 (https://app-poredomedia2.azurewebsites.net)";

    /// <param name="take">0 for the best match; each later take picks the next result down.</param>
    public async Task FindAsync(string words, int take, string outputPath, CancellationToken ct)
    {
        var limit = Math.Clamp(take + 1, 1, 10);
        var url = $"https://commons.wikimedia.org/w/api.php?action=query&generator=search&gsrnamespace=6&gsrlimit={limit}&gsrsearch={Uri.EscapeDataString(words + " filetype:bitmap")}&prop=imageinfo&iiprop=url&iiurlwidth=1280&format=json";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Wikimedia Commons answered {(int)response.StatusCode}.");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var imageUrl = ExtractImageUrl(json.RootElement, take);
        if (imageUrl is null)
            throw new InvalidOperationException(take == 0
                ? $"No Wikimedia Commons photo matches “{words}”."
                : $"There are no more Wikimedia Commons photos of “{words}”.");

        var uri = new Uri(imageUrl);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith(".wikimedia.org", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Wikimedia Commons returned an unexpected image host.");

        using var imgReq = new HttpRequestMessage(HttpMethod.Get, uri);
        imgReq.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var imgRes = await http.SendAsync(imgReq, ct);
        imgRes.EnsureSuccessStatusCode();
        await File.WriteAllBytesAsync(outputPath, await imgRes.Content.ReadAsByteArrayAsync(ct), ct);
    }

    /// <summary>Extracts the <paramref name="take"/>-th thumbnail/full URL from a Wikimedia Commons query payload.</summary>
    public static string? ExtractImageUrl(JsonElement root, int take)
    {
        if (!root.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages))
            return null;

        var candidates = new List<string>();
        foreach (var page in pages.EnumerateObject())
        {
            if (!page.Value.TryGetProperty("imageinfo", out var info) || info.GetArrayLength() == 0)
                continue;
            var first = info[0];
            var href = first.TryGetProperty("thumburl", out var thumb) && thumb.ValueKind == JsonValueKind.String
                ? thumb.GetString()
                : first.TryGetProperty("url", out var full) && full.ValueKind == JsonValueKind.String
                    ? full.GetString()
                    : null;
            if (!string.IsNullOrWhiteSpace(href))
                candidates.Add(href);
        }

        return take >= 0 && take < candidates.Count ? candidates[take] : null;
    }
}
