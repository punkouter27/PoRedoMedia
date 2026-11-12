using System.Text.Json;

namespace PoRedoMedia.Client.Services;

/// <summary>Extracts the user-facing <c>detail</c> or <c>title</c> from RFC 7807 problem responses.</summary>
public static class ProblemDetailsParser
{
    public static async Task<string> ReadReasonAsync(HttpResponseMessage response)
    {
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (problem.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String
                && detail.GetString() is { Length: > 0 } text)
            {
                return text;
            }

            if (problem.RootElement.TryGetProperty("title", out var title)
                && title.ValueKind == JsonValueKind.String
                && title.GetString() is { Length: > 0 } titleText)
            {
                return titleText;
            }
        }
        catch (JsonException)
        {
            // Not a problem document; fall through.
        }

        return $"The request failed ({(int)response.StatusCode}).";
    }
}
