using System.Text.Json;

namespace PoRedoMedia.Movie.Pictures;

/// <summary>Reaction GIFs and stills from Tenor v2 for Create Movie and meme visuals when a Tenor API key is configured.</summary>
public sealed class TenorStock(string apiKey, HttpClient http)
{
    public async Task FindAsync(string words, int take, string outputPath, CancellationToken ct)
    {
        var limit = Math.Clamp(take + 1, 1, 10);
        var url = $"https://tenor.googleapis.com/v2/search?q={Uri.EscapeDataString(words)}&key={Uri.EscapeDataString(apiKey)}&client_key=poredomedia&limit={limit}&media_filter=gif,mediumgif";
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Tenor answered {(int)response.StatusCode}. Check the Tenor key.");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var mediaUrl = ExtractMediaUrl(json.RootElement, take);
        if (mediaUrl is null)
            throw new InvalidOperationException(take == 0
                ? $"No Tenor reaction matches “{words}”."
                : $"There are no more Tenor reactions for “{words}”.");

        var uri = new Uri(mediaUrl);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith(".tenor.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Tenor returned an unexpected media host.");

        await File.WriteAllBytesAsync(outputPath, await http.GetByteArrayAsync(uri, ct), ct);
    }

    public static string? ExtractMediaUrl(JsonElement root, int take)
    {
        if (!root.TryGetProperty("results", out var results) || results.GetArrayLength() <= take || take < 0)
            return null;

        var item = results[take];
        if (!item.TryGetProperty("media_formats", out var formats))
            return null;

        foreach (var key in new[] { "mediumgif", "gif" })
        {
            if (formats.TryGetProperty(key, out var fmt) && fmt.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                return url.GetString();
        }
        return null;
    }
}
