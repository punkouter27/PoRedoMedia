using System.Text.Json;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Client.Services;

/// <summary>
/// Reads the saved recipes (<see cref="UiStore.Recipes"/>) one by one, so a recipe saved before a
/// function was removed (Upscale, Cutout) loads with that step left out instead of the whole list
/// failing to read. A recipe with no function left is dropped.
/// </summary>
public static class RecipeBook
{
    public static Dictionary<string, RunRequest> Parse(string? json)
    {
        var recipes = new Dictionary<string, RunRequest>();
        if (string.IsNullOrWhiteSpace(json))
            return recipes;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return recipes;

            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                    continue;

                MediaFunction[] functions = [.. Property(entry.Value, "functions") is { ValueKind: JsonValueKind.Array } list
                    ? list.EnumerateArray().Select(Function).OfType<MediaFunction>().Distinct()
                    : []];
                if (functions.Length == 0)
                    continue;

                var options = Property(entry.Value, "options") is { ValueKind: JsonValueKind.Object } stored
                    ? stored.EnumerateObject().Where(o => o.Value.ValueKind == JsonValueKind.String).ToDictionary(o => o.Name, o => o.Value.GetString()!)
                    : [];
                recipes[entry.Name] = new RunRequest(Guid.Empty, functions, options);
            }
        }
        catch (JsonException)
        {
            // Not JSON at all: nothing can be saved from it.
        }

        return recipes;
    }

    /// <summary>A function this version knows by name; null for one that was removed or never existed.</summary>
    private static MediaFunction? Function(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && Enum.TryParse<MediaFunction>(value.GetString(), out var function)
            && Enum.IsDefined(function) && !char.IsDigit(value.GetString()![0])
            ? function
            : null;

    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return null;
    }
}
