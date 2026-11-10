using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PoRedoMedia.Movie.Library;

/// <summary>
/// How the app reads and writes JSON. Every type it saves or reads is described as the app is built, not looked up by
/// reflection as it runs, so trimming can never drop a field that is read, and the app can be compiled ahead of time.
/// A type that is not listed (in <see cref="CoreJson"/>, or a context of the caller's own given to <see cref="Knowing"/>)
/// fails the first time it is read or written: list it.
/// </summary>
public static class Json
{
    /// <summary>The serializer's own defaults, knowing every listed type.</summary>
    public static JsonSerializerOptions Plain { get; } = Knowing(new JsonSerializerOptions());

    /// <summary>Gives <paramref name="options"/> every listed type, and first those of a caller's own context.</summary>
    public static JsonSerializerOptions Knowing(JsonSerializerOptions options, IJsonTypeInfoResolver? own = null)
    {
        options.TypeInfoResolver = own is null ? CoreJson.Default : JsonTypeInfoResolver.Combine(own, CoreJson.Default);
        return options;
    }

    public static string Write<T>(T value, JsonSerializerOptions? options = null) => JsonSerializer.Serialize(value, Info<T>(options));

    public static T? Read<T>(string json, JsonSerializerOptions? options = null) => JsonSerializer.Deserialize(json, Info<T>(options));

    private static JsonTypeInfo<T> Info<T>(JsonSerializerOptions? options) => (JsonTypeInfo<T>)(options ?? Plain).GetTypeInfo(typeof(T));
}

// The core's saved and remembered types. Each is described for whatever options it is read with, so the episode's own
// (words for its enums, strict reading) still apply.
[JsonSerializable(typeof(Episode))]
[JsonSerializable(typeof(SeriesKit))]
[JsonSerializable(typeof(List<GroundingSnippet>))]
[JsonSerializable(typeof(IReadOnlyList<GroundingSnippet>))]
[JsonSerializable(typeof(List<WordTiming>))]
[JsonSerializable(typeof(IReadOnlyList<WordTiming>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(PublishNotes))]
[JsonSerializable(typeof(FootageSeen))]
internal sealed partial class CoreJson : JsonSerializerContext;
