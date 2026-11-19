namespace PoRedoMedia.Mobile.Services;

/// <summary>Keeps transfers and render tracking alive in the background and shows their progress (items 5 and 10).</summary>
public interface IBackgroundWork
{
    /// <summary>Android: a data-sync foreground service with an ongoing, updating progress notification. iOS: a background task.</summary>
    void Progress(string title, string text, int percent);

    void End();

    /// <summary>A one-off notification; <paramref name="link"/> (poredomedia://media/{id}) opens when tapped.</summary>
    void Notify(string title, string text, string? link);

    Task<bool> RequestNotificationsAsync();
}

/// <summary>Item 2: puts a finished picture or video into the phone's gallery (Android MediaStore, iOS Photos).</summary>
public interface IGallery
{
    /// <summary>Null when saved, otherwise why not.</summary>
    Task<string?> SaveAsync(string localPath, bool video);
}

/// <summary>Item 5: downloads a blob link in the background (Android DownloadManager; iOS a background task) to the app's files.</summary>
public interface IDownloader
{
    Task<string?> DownloadAsync(Uri link, string fileName, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>Item 8: the platform's own video player, with picture-in-picture.</summary>
public interface IVideoPlayer
{
    Task PlayAsync(Uri source, string title);
}

/// <summary>Makes a picture uploadable: HEIC and anything over 10 MB (or over 4096 px) becomes a JPEG.</summary>
public interface IImagePrep
{
    Task<string> PrepareAsync(string path);
}
