using AVFoundation;
using AVKit;
using Foundation;
using Photos;
using PoRedoMedia.Mobile.Services;
using UIKit;
using UserNotifications;

namespace PoRedoMedia.Mobile;

/// <summary>
/// iOS keeps an app working for ~30 s after it leaves the screen; a background task asks for that time, and the
/// queue resumes where it stopped when the app comes back. (A Live Activity needs an ActivityKit widget extension,
/// which a MAUI project cannot build, so iOS shows progress in the app and notifies when a remix is ready.)
/// </summary>
public sealed class IosBackgroundWork : IBackgroundWork
{
    private nint _task = UIApplication.BackgroundTaskInvalid;

    public void Progress(string title, string text, int percent) => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (_task == UIApplication.BackgroundTaskInvalid)
        {
            _task = UIApplication.SharedApplication.BeginBackgroundTask("poredomedia-transfer", End);
        }
    });

    public void End() => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (_task != UIApplication.BackgroundTaskInvalid)
        {
            UIApplication.SharedApplication.EndBackgroundTask(_task);
            _task = UIApplication.BackgroundTaskInvalid;
        }
    });

    public void Notify(string title, string text, string? link)
    {
        UNMutableNotificationContent content = new() { Title = title, Body = text, Sound = UNNotificationSound.Default };
        if (link is not null)
        {
            content.UserInfo = NSDictionary.FromObjectAndKey(new NSString(link), new NSString("link"));
        }

        UNUserNotificationCenter.Current.AddNotificationRequest(UNNotificationRequest.FromIdentifier(Guid.NewGuid().ToString(), content, null), _ => { });
    }

    public async Task<bool> RequestNotificationsAsync() =>
        (await UNUserNotificationCenter.Current.RequestAuthorizationAsync(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound)).Item1;
}

/// <summary>Item 2: saves into Photos with add-only access.</summary>
public sealed class IosGallery : IGallery
{
    public async Task<string?> SaveAsync(string localPath, bool video)
    {
        if (await PHPhotoLibrary.RequestAuthorizationAsync(PHAccessLevel.AddOnly) is not (PHAuthorizationStatus.Authorized or PHAuthorizationStatus.Limited))
        {
            return "Allow PoRedoMedia to add to Photos in Settings.";
        }

        NSUrl url = NSUrl.FromFilename(localPath);
        TaskCompletionSource<string?> done = new();
        PHPhotoLibrary.SharedPhotoLibrary.PerformChanges(
            () => _ = video ? PHAssetChangeRequest.FromVideo(url) : PHAssetChangeRequest.FromImage(url),
            (ok, error) => done.TrySetResult(ok ? null : error?.LocalizedDescription ?? "Photos refused the file."));
        return await done.Task;
    }
}

/// <summary>Item 5 on iOS: a plain download inside a background task (the link needs no credentials).</summary>
public sealed class IosDownloader : IDownloader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public async Task<string?> DownloadAsync(Uri link, string fileName, IProgress<double>? progress, CancellationToken ct)
    {
        nint task = UIApplication.SharedApplication.BeginBackgroundTask("poredomedia-download", () => { });
        try
        {
            string target = Path.Combine(FileSystem.AppDataDirectory, "downloads", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using HttpResponseMessage resp = await Http.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            await using Stream body = await resp.Content.ReadAsStreamAsync(ct);
            await using FileStream file = File.Create(target);
            byte[] buffer = new byte[1 << 16];
            long have = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                have += read;
                if (total > 0)
                {
                    progress?.Report(have / (double)total);
                }
            }

            return target;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        finally
        {
            UIApplication.SharedApplication.EndBackgroundTask(task);
        }
    }
}

/// <summary>Item 8: AVPlayerViewController, which has picture-in-picture built in (with background audio enabled).</summary>
public sealed class IosVideoPlayer : IVideoPlayer
{
    public Task PlayAsync(Uri source, string title) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        AVAudioSession.SharedInstance().SetCategory(AVAudioSessionCategory.Playback, out _);
        AVPlayerViewController controller = new()
        {
            Player = new AVPlayer(source.IsFile ? NSUrl.FromFilename(source.LocalPath) : new NSUrl(source.ToString())),
            AllowsPictureInPicturePlayback = true,
            CanStartPictureInPictureAutomaticallyFromInline = true,
        };
        Platform.GetCurrentUIViewController()?.PresentViewController(controller, true, () => controller.Player?.Play());
    });
}

/// <summary>HEIC (the iPhone camera's default) and anything too big become a JPEG the API accepts.</summary>
public sealed class IosImagePrep : IImagePrep
{
    public Task<string> PrepareAsync(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" && new FileInfo(path).Length <= 10L * 1024 * 1024)
        {
            return Task.FromResult(path);
        }

        using UIImage? image = UIImage.FromFile(path);
        if (image is null)
        {
            return Task.FromResult(path);
        }

        nfloat scale = (nfloat)Math.Min(1, 4096 / Math.Max(image.Size.Width, image.Size.Height));
        CoreGraphics.CGSize size = new(image.Size.Width * scale, image.Size.Height * scale);
        using UIImage sized = new UIGraphicsImageRenderer(size).CreateImage(_ => image.Draw(new CoreGraphics.CGRect(CoreGraphics.CGPoint.Empty, size)));
        string target = Path.Combine(FileSystem.CacheDirectory, $"{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}.jpg");
        sized.AsJPEG(0.88f)!.Save(target, true);
        return Task.FromResult(target);
    }
}
