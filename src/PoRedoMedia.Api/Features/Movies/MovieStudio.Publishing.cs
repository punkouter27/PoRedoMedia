using PoRedoMedia.Api.Features.Publishing;
using PoRedoMedia.Movie.Library;
using PoRedoMedia.Movie.Render;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Movies;

/// <summary>The sites a movie is published to, and where each user's connection to them is kept.</summary>
public sealed record Publishers(IYouTubePublisher YouTube, ITikTokPublisher TikTok, PlatformTokenStore Tokens);

public sealed partial class MovieStudio
{
    /// <summary>
    /// Publish in one go: the finished video (with its thumbnail) to YouTube and the upright copy to TikTok (a draft in
    /// the inbox, or a direct post), as one job with its progress shown. Free: only FFmpeg works here. When every site
    /// took it, the movie is removed from the server, as a download removes it; when one refused, it stays, to try again.
    /// </summary>
    /// <returns>Why it could not start, or null.</returns>
    public string? Publish(OpenMovie open, PublishRequest request, Publishers sites)
    {
        if (!engine.FfmpegAvailable) return "FFmpeg is needed to publish, and it was not found on this server.";
        if (EpisodeBuilder.LatestMaster(open.Folder) is not { } master) return "Render the finished video first.";
        var info = PackageInfoOf(open);
        var id = open.Id;
        return Start("Publishing", open.Id, open.Folder, async (job, ct) =>
        {
            var work = Files.NewScratchFolder("publish");
            var said = new List<string>();
            try
            {
                job.Phase = (0, 0.4);
                var (upright, thumbnail) = await engine.Builder.PreparePartsAsync(master, work, Rendering(job), ct);
                if (request.YouTube)
                {
                    job.Phase = (0.4, request.TikTok ? 0.35 : 0.6);
                    said.Add(await ToYouTubeAsync(job, sites, master, thumbnail, info, request.YouTubePrivacy, ct));
                }
                if (request.TikTok)
                {
                    job.Phase = (request.YouTube ? 0.75 : 0.4, request.YouTube ? 0.25 : 0.6);
                    said.Add(await ToTikTokAsync(job, sites, upright, info, request, ct));
                }
            }
            catch (PublishException e)
            {
                // What already went out is said with the refusal, so nothing is sent twice by mistake.
                job.Message = said.Count > 0 ? string.Join(' ', said) : null;
                throw new InvalidOperationException(e.Message, e);
            }
            finally
            {
                DeleteScratchFolder(work);
            }
            job.Message = string.Join(' ', said) + " The movie has been removed from this server.";
            job.MovieId = null; // the movie goes; nothing is reopened when the job ends
            Delete(id);
        });
    }

    private async Task<string> ToYouTubeAsync(Job job, Publishers sites, string master, string thumbnail, PackageInfo info, string privacy, CancellationToken ct)
    {
        Report(job, "Signing in to YouTube", 0.01);
        var access = await AccessAsync(sites, Platform.YouTube, ct);
        var uploaded = await Reconnecting(sites, Platform.YouTube, () => sites.YouTube.UploadAsync(access,
            master, new YouTubeVideo(info.Title, info.Description, info.Tags, info.Category, privacy, info.MadeForKids),
            new Sink<double>(p => Report(job, "Sending the video to YouTube", 0.02 + 0.9 * p)), ct));
        var link = $"https://youtu.be/{uploaded.VideoId}";
        var note = "";
        try
        {
            Report(job, "Setting the YouTube thumbnail", 0.95);
            await sites.YouTube.SetThumbnailAsync(access, uploaded.VideoId, thumbnail, ct);
        }
        catch (PublishException e) when (e.Failure == PublishFailure.Refused)
        {
            note = " Its thumbnail was not set: YouTube allows custom thumbnails only on a verified channel (verify it in YouTube Studio, then set the thumbnail there).";
        }
        if (!string.Equals(uploaded.Privacy, privacy, StringComparison.OrdinalIgnoreCase))
            note += $" YouTube made it {uploaded.Privacy}, not {privacy}: videos sent through an app Google has not audited stay private until it is. Change it in YouTube Studio.";
        return $"On YouTube ({uploaded.Privacy}): {link}.{note}";
    }

    private async Task<string> ToTikTokAsync(Job job, Publishers sites, string upright, PackageInfo info, PublishRequest request, CancellationToken ct)
    {
        Report(job, "Signing in to TikTok", 0.01);
        var refresh = await sites.Tokens.GetAsync(user, Platform.TikTok, ct) ?? throw new PublishException(PublishFailure.Reconnect, "Connect TikTok first.");
        var tokens = await Reconnecting(sites, Platform.TikTok, () => sites.TikTok.RefreshAsync(refresh, ct));
        if (tokens.Refresh is { Length: > 0 } rotated && rotated != refresh)
            await sites.Tokens.SaveAsync(user, Platform.TikTok, rotated, ct); // TikTok hands out a new one each time
        var direct = request.TikTokMode == "direct" && sites.TikTok.DirectPost;
        if (direct && !(await sites.TikTok.PrivacyOptionsAsync(tokens.Access, ct)).Contains(request.TikTokPrivacy))
            throw new PublishException(PublishFailure.Refused,
                $"TikTok does not let this account post as {request.TikTokPrivacy} through this app (an app TikTok has not reviewed may post only as Only me). Choose Only me, or send it as a draft.");
        var sent = await Reconnecting(sites, Platform.TikTok, () => sites.TikTok.SendAsync(tokens.Access, upright,
            direct ? PublishPackage.TikTokCaption(info) : null, direct ? request.TikTokPrivacy : null,
            new Sink<double>(p => Report(job, "Sending the video to TikTok", 0.02 + 0.95 * p)), ct));
        return direct
            ? $"Posted to TikTok ({request.TikTokPrivacy}; it may take a minute to appear{(sent.Status == "PUBLISH_COMPLETE" ? "" : ", TikTok is still processing it")})."
            : "Sent to your TikTok inbox as a draft: open TikTok, check your notifications, add the caption (it is in the download package's tiktok.txt) and post it.";
    }

    /// <summary>A fresh YouTube pass from the stored refresh token.</summary>
    private async Task<string> AccessAsync(Publishers sites, Platform site, CancellationToken ct)
    {
        var refresh = await sites.Tokens.GetAsync(user, site, ct) ?? throw new PublishException(PublishFailure.Reconnect, $"Connect {site} first.");
        return await Reconnecting(sites, site, () => sites.YouTube.RefreshAsync(refresh, ct));
    }

    /// <summary>A connection the site no longer accepts is forgotten here too, so the dialog asks to connect again.</summary>
    private async Task<T> Reconnecting<T>(Publishers sites, Platform site, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (PublishException e) when (e.Failure == PublishFailure.Reconnect)
        {
            await sites.Tokens.DeleteAsync(user, site, CancellationToken.None);
            throw;
        }
    }

    private static void DeleteScratchFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Swept with the rest of the scratch folder later.
        }
    }
}
