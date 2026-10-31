using System.Text.Json;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.PhotoToVideo;

/// <summary>Turns the current image into a short clip with sound, following the user's description.</summary>
public sealed class PhotoToVideoStep(
    IVideoGenerationService video, BlobStorageService blobs, MediaOutputs outputs, FFmpegProcess ffmpeg) : IRunStep
{
    public IReadOnlySet<MediaFunction> Handles { get; } = new HashSet<MediaFunction> { MediaFunction.PhotoToVideo };

    public async Task ExecuteAsync(RunContext context, CancellationToken ct)
    {
        var prompt = context.Option(RunOptions.VideoPrompt)?.Trim();
        if (prompt is null || prompt.Length is < 3 or > 1200)
            throw new RunStepException("Describe what should happen in the video, in 3 to 1,200 characters.");

        var image = ImageBytes.ForProcessing(await blobs.ReadAllBytesAsync(context.Current.SourcePath, ct));
        await context.ReportAsync("Making the video. This takes a few minutes.");
        var clip = await video.GenerateAsync(image, prompt, ct);

        var item = await outputs.SaveAsync(
            context.Current, MediaFunction.PhotoToVideo, MediaKind.Video, clip, "video/mp4", ".mp4", ct,
            durationSeconds: await DurationAsync(clip, ct));
        await context.AddOutputAsync(item);
    }

    private async Task<double?> DurationAsync(byte[] clip, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"poredomedia-clip-{Guid.NewGuid():N}.mp4");
        try
        {
            await File.WriteAllBytesAsync(path, clip, ct);
            return await ffmpeg.DurationSecondsAsync(path, ct) is > 0 and var seconds ? seconds : null;
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// Google Gemini Omni Flash through the Interactions API: the picture and the description go in, a
/// clip of 3 to 10 seconds with its own sound comes back in the same answer. Omni replaced Veo,
/// whose preview models Google shut down on 2026-10-22.
/// </summary>
public sealed class OmniVideoService(IConfiguration configuration, IHttpClientFactory http) : IVideoGenerationService
{
    public const string HttpClientName = "GoogleVideo";
    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/interactions";

    private readonly string _model = configuration[ConfigKeys.GoogleVideoModel] is { Length: > 0 } model ? model : "gemini-omni-1.1-flash";

    public async Task<byte[]> GenerateAsync(byte[] image, string prompt, CancellationToken ct = default)
    {
        var body = new
        {
            model = _model,
            input = new object[]
            {
                new { type = "image", data = Convert.ToBase64String(image), mime_type = "image/jpeg" },
                new { type = "text", text = prompt },
            },
            generation_config = new { video_config = new { task = "image_to_video" } },
            response_format = new { type = "video", resolution = "720p" },
            // Not kept on Google's side: the clip is never edited there afterwards.
            store = false,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(body) };
        request.Headers.Add("x-goog-api-key", configuration[ConfigKeys.GoogleApiKey]);
        request.Options.Set(AiMeterHandler.Kind, "video");

        using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw Rejected(await response.Content.ReadAsStringAsync(ct), (int)response.StatusCode);

        using var answer = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return TryExtractVideo(answer.RootElement, out var clip)
            ? clip
            : throw new RunStepException("The video service finished without a clip, which usually means its safety filter blocked it. Try a different description or picture.");
    }

    private static RunStepException Rejected(string body, int status)
    {
        // Google says "out of credit" with a 402, and sometimes with a 429 that carries the same text.
        if (status == 402 || body.Contains("credits are depleted", StringComparison.OrdinalIgnoreCase))
            return new RunStepException("The Google AI account is out of credit. Top it up in AI Studio, then try again.");
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message))
                return new RunStepException($"The video service refused the request: {message.GetString()}");
        }
        catch (JsonException)
        {
            // Not JSON; fall through.
        }

        return new RunStepException($"The video service refused the request ({status}).");
    }

    /// <summary>The first video in a completed interaction's <c>steps[].content[]</c>; none when it was blocked.</summary>
    internal static bool TryExtractVideo(JsonElement root, out byte[] clip)
    {
        clip = [];
        if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var step in steps.EnumerateArray())
        {
            if (!step.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var type) && type.ValueEquals("video")
                    && part.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
                {
                    clip = data.GetBytesFromBase64();
                    return clip.Length > 0;
                }
            }
        }

        return false;
    }
}

/// <summary>Stands in for Omni with a real, playable two-second clip of the picture, made locally.</summary>
public sealed class MockVideoService(FFmpegProcess ffmpeg) : IVideoGenerationService
{
    public async Task<byte[]> GenerateAsync(byte[] image, string prompt, CancellationToken ct = default)
    {
        var stem = Path.Combine(Path.GetTempPath(), $"poredomedia-mockvideo-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(stem + ".jpg", image, ct);
            var exit = await ffmpeg.RunAsync(
                $"-y -loop 1 -i \"{stem}.jpg\" -f lavfi -i anullsrc=r=44100:cl=stereo -t 2 -r 10 -vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" -pix_fmt yuv420p -shortest \"{stem}.mp4\"",
                "mock video", ct);
            return exit == 0 ? await File.ReadAllBytesAsync(stem + ".mp4", ct) : throw new RunStepException("The mock video could not be made. Is ffmpeg installed?");
        }
        finally
        {
            File.Delete(stem + ".jpg");
            File.Delete(stem + ".mp4");
        }
    }
}
