using System.Text;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Render;

/// <summary>
/// Other forms of a result, all under <c>/api/media/{id}/export/</c>: a looping GIF of a video,
/// the video re-framed for a social feed, its captions as an SRT file, and its roast as a sound file.
/// </summary>
public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExports(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/media/{id}/export");
        group.MapPost("/gif", (MediaId id, [AsParameters] Encoder encoder, CancellationToken ct) =>
            encoder.EncodeOnceAsync(id, MediaAnalysisPaths.Gif(id.Value), "GIF", "image/gif", FFmpegArgs.GifArgs, ct))
            .AddEndpointFilter<AntiforgeryValidationFilter>();
        group.MapPost("/social", (MediaId id, string? aspect, [AsParameters] Encoder encoder, CancellationToken ct) =>
            FFmpegArgs.SocialFrame(aspect) is null
                ? Task.FromResult(UnknownAspect())
                : encoder.EncodeOnceAsync(
                    id, MediaAnalysisPaths.Social(id.Value, aspect!), "social video", "video/mp4",
                    (source, output) => FFmpegArgs.SocialArgs(source, output, aspect)!, ct))
            .AddEndpointFilter<AntiforgeryValidationFilter>();
        group.MapGet("/{kind}", ExportAsync);
        return app;
    }

    private static IResult UnknownAspect() => Results.Problem(
        detail: $"Pick one of {string.Join(", ", SocialExport.Aspects)}.", statusCode: StatusCodes.Status400BadRequest);

    /// <summary>What it takes to encode an export of a video once.</summary>
    public sealed record Encoder(
        HttpContext Http, IMediaRepository Media, BlobStorageService Blobs, StorageClients Storage,
        FFmpegProcess FFmpeg, IRenderQuota Quota, Features.Runs.RunDispatcher Dispatcher)
    {
        /// <summary>
        /// Encodes an export of the caller's video to <paramref name="path"/>. The first request
        /// encodes it, which costs one run credit; it is kept with the video, so asking again is
        /// free. A write, not a link: a link that spends a credit can be followed twice, and its
        /// refusal would replace the page it was clicked on.
        /// </summary>
        public async Task<IResult> EncodeOnceAsync(
            MediaId id, string path, string what, string contentType, Func<string, string, string> args, CancellationToken ct)
        {
            var owner = UserId.From(Http.User);
            if (await Media.GetAsync(owner, id, ct) is not { Kind: MediaKind.Video, Status: MediaStatus.Ready } item)
                return Results.NotFound();

            if (await Blobs.ExistsAsync(path, ct))
                return Results.NoContent();

            // The video's lane, as a run takes it: a second click, or a run on it, waits its turn.
            if (!Dispatcher.TryReserve(id))
                return Results.Problem(detail: "This video is busy. Try again in a moment.", statusCode: StatusCodes.Status409Conflict);

            var file = Path.Combine(Path.GetTempPath(), $"poredomedia-{id}-{Path.GetFileName(path)}");
            var spent = false;
            try
            {
                var (allowed, status) = await Quota.TryConsumeAsync(owner, ct);
                if (!allowed)
                {
                    return Results.Problem(
                        detail: $"Making a {what} uses a run, and all {status.Limit} for today are used. More are available at {status.ResetsAt:HH:mm} UTC.",
                        statusCode: StatusCodes.Status429TooManyRequests);
                }

                spent = true;
                // ffmpeg reads the video straight from storage; only the export touches this disk.
                var link = Storage.CreateReadLink(item.SourcePath, TimeSpan.FromMinutes(10));
                if (await FFmpeg.RunAsync(args(link.ToString(), file), id, ct) != 0 || !File.Exists(file))
                {
                    spent = false;
                    await Quota.RefundAsync(owner, CancellationToken.None);
                    return Results.Problem(detail: $"The {what} could not be made from this video.", statusCode: StatusCodes.Status500InternalServerError);
                }

                await Blobs.UploadFileAsync(path, file, contentType, ct);
                return Results.NoContent();
            }
            catch when (spent)
            {
                await Quota.RefundAsync(owner, CancellationToken.None);
                throw;
            }
            finally
            {
                Dispatcher.Release(id);
                try { File.Delete(file); } catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// One export of the caller's item: <c>gif</c> and <c>social?aspect=</c> once they have been
    /// made, <c>srt</c> for the captions burned into a video, <c>roast</c> for the insults alone as
    /// the voice performed them. 404 when the item has no such thing.
    /// </summary>
    private static async Task<IResult> ExportAsync(
        MediaId id, string kind, string? aspect, HttpContext http, IMediaRepository media, BlobStorageService blobs, BlobDelivery delivery,
        CancellationToken ct)
    {
        if (await media.GetAsync(UserId.From(http.User), id, ct) is not { Status: MediaStatus.Ready } item)
            return Results.NotFound();

        switch (kind)
        {
            case "gif" when item.Kind == MediaKind.Video:
                return await delivery.ServeAsync(http, MediaAnalysisPaths.Gif(id.Value), "meme.gif", ct: ct);

            case "social" when item.Kind == MediaKind.Video && FFmpegArgs.SocialFrame(aspect) is not null:
                return await delivery.ServeAsync(http, MediaAnalysisPaths.Social(id.Value, aspect!), $"social-{aspect}.mp4", ct: ct);

            case "srt" when await blobs.ExistsAsync(MediaAnalysisPaths.Transcript(id.Value), ct):
                var srt = SrtWriter.Write(await MediaTranscript.LoadAsync(blobs, id, ct));
                return Results.File(Encoding.UTF8.GetBytes(srt), "application/x-subrip", "captions.srt");

            case "roast":
                foreach (var extension in new[] { "mp3", "wav" })
                {
                    var path = MediaAnalysisPaths.RoastClip(id.Value, 0, extension);
                    if (await blobs.ExistsAsync(path, ct))
                        return await delivery.ServeAsync(http, path, $"roast.{extension}", ct: ct);
                }
                return Results.NotFound();

            default:
                return Results.NotFound();
        }
    }
}
