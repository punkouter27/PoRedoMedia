using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Widget;
using AndroidX.Core.App;
using PoRedoMedia.Mobile.Services;
using AndroidApp = Android.App.Application;
using AUri = Android.Net.Uri;

namespace PoRedoMedia.Mobile;

/// <summary>Items 5 and 10: a data-sync foreground service while anything moves, its notification showing the progress.</summary>
public sealed class AndroidBackgroundWork : IBackgroundWork
{
    public const string ProgressChannel = "progress", DoneChannel = "done";
    private static Context Ctx => AndroidApp.Context;
    private bool _running;

    public void Progress(string title, string text, int percent)
    {
        Channels(Ctx);
        Intent intent = new Intent(Ctx, typeof(TransferService)).PutExtra("title", title).PutExtra("text", text).PutExtra("percent", percent);
        if (!_running)
        {
            Ctx.StartForegroundService(intent);
            _running = true;
            return;
        }

        NotificationManagerCompat.From(Ctx)!.Notify(TransferService.Id, TransferService.Build(Ctx, title, text, percent));
    }

    public void End()
    {
        if (_running)
        {
            Ctx.StopService(new Intent(Ctx, typeof(TransferService)));
            _running = false;
        }
    }

    public void Notify(string title, string text, string? link)
    {
        Channels(Ctx);
        var builder = new NotificationCompat.Builder(Ctx, DoneChannel);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle(title);
        builder.SetContentText(text);
        builder.SetAutoCancel(true);
        builder.SetPriority(NotificationCompat.PriorityHigh);
        if (link is not null)
        {
            Intent open = new(Intent.ActionView, AUri.Parse(link));
            open.SetPackage(Ctx.PackageName);
            builder.SetContentIntent(PendingIntent.GetActivity(Ctx, link.GetHashCode(StringComparison.Ordinal), open, PendingIntentFlags.Immutable));
        }

        NotificationManagerCompat.From(Ctx)!.Notify(System.Environment.TickCount & 0x3FFFFFFF, builder.Build()!);
    }

    public async Task<bool> RequestNotificationsAsync()
    {
        Channels(Ctx);
        return !OperatingSystem.IsAndroidVersionAtLeast(33) || await Permissions.RequestAsync<Permissions.PostNotifications>() == PermissionStatus.Granted;
    }

    internal static void Channels(Context context)
    {
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(ProgressChannel, "Uploads and renders", NotificationImportance.Low));
        manager.CreateNotificationChannel(new NotificationChannel(DoneChannel, "Finished remixes", NotificationImportance.High));
    }
}

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class TransferService : Service
{
    public const int Id = 909;
    private PowerManager.WakeLock? _wake;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        AndroidBackgroundWork.Channels(this);
        Notification note = Build(this, intent?.GetStringExtra("title") ?? "PoRedoMedia", intent?.GetStringExtra("text") ?? "Working…", intent?.GetIntExtra("percent", 0) ?? 0);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            StartForeground(Id, note, ForegroundService.TypeDataSync);
        }
        else
        {
            StartForeground(Id, note);
        }

        if (_wake is null)
        {
            _wake = ((PowerManager)GetSystemService(PowerService)!).NewWakeLock(WakeLockFlags.Partial, "poredomedia:transfer");
            _wake!.Acquire((long)TimeSpan.FromHours(1).TotalMilliseconds); // a safety cap; released when the work ends
        }

        return StartCommandResult.NotSticky;
    }

    internal static Notification Build(Context context, string title, string text, int percent)
    {
        var builder = new NotificationCompat.Builder(context, AndroidBackgroundWork.ProgressChannel);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle(title);
        builder.SetContentText(text);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetProgress(100, Math.Clamp(percent, 0, 100), percent <= 0);
        builder.SetContentIntent(PendingIntent.GetActivity(context, 0, context.PackageManager!.GetLaunchIntentForPackage(context.PackageName!)!, PendingIntentFlags.Immutable));
        return builder.Build()!;
    }

    public override void OnDestroy()
    {
        if (_wake?.IsHeld == true)
        {
            _wake.Release();
        }

        base.OnDestroy();
    }
}

/// <summary>Item 2: copies a file into Pictures/PoRedoMedia or Movies/PoRedoMedia through MediaStore (no storage permission).</summary>
public sealed class AndroidGallery : IGallery
{
    public async Task<string?> SaveAsync(string localPath, bool video)
    {
        ContentResolver resolver = AndroidApp.Context.ContentResolver!;
        ContentValues values = new();
        values.Put(MediaStore.IMediaColumns.DisplayName, System.IO.Path.GetFileName(localPath));
        values.Put(MediaStore.IMediaColumns.MimeType, video ? "video/mp4" : MimeFor(localPath));
        values.Put(MediaStore.IMediaColumns.RelativePath, video ? "Movies/PoRedoMedia" : "Pictures/PoRedoMedia");
        values.Put(MediaStore.IMediaColumns.IsPending, 1);
        AUri collection = video ? MediaStore.Video.Media.GetContentUri(MediaStore.VolumeExternalPrimary)! : MediaStore.Images.Media.GetContentUri(MediaStore.VolumeExternalPrimary)!;
        if (resolver.Insert(collection, values) is not { } item)
        {
            return "The gallery refused the file.";
        }

        await using (Stream target = resolver.OpenOutputStream(item)!)
        await using (FileStream source = File.OpenRead(localPath))
        {
            await source.CopyToAsync(target);
        }

        values.Clear();
        values.Put(MediaStore.IMediaColumns.IsPending, 0);
        resolver.Update(item, values, null, null);
        return null;
    }

    private static string MimeFor(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };
}

/// <summary>Item 5: the system DownloadManager — it keeps going with the app closed and shows its own progress.</summary>
public sealed class AndroidDownloader : IDownloader
{
    public async Task<string?> DownloadAsync(Uri link, string fileName, IProgress<double>? progress, CancellationToken ct)
    {
        Context ctx = AndroidApp.Context;
        var manager = (DownloadManager)ctx.GetSystemService(Context.DownloadService)!;
        string dir = ctx.GetExternalFilesDir(Android.OS.Environment.DirectoryDownloads)!.AbsolutePath;
        string target = System.IO.Path.Combine(dir, fileName);
        File.Delete(target);
        DownloadManager.Request request = new(AUri.Parse(link.ToString()));
        request.SetTitle(fileName);
        request.SetNotificationVisibility(DownloadVisibility.Visible);
        request.SetDestinationInExternalFilesDir(ctx, Android.OS.Environment.DirectoryDownloads, fileName);
        long id = manager.Enqueue(request);

        while (!ct.IsCancellationRequested)
        {
            using ICursor? row = manager.InvokeQuery(new DownloadManager.Query().SetFilterById(id));
            if (row is null || !row.MoveToFirst())
            {
                return null;
            }

            var status = (DownloadStatus)row.GetInt(row.GetColumnIndex(DownloadManager.ColumnStatus));
            long done = row.GetLong(row.GetColumnIndex(DownloadManager.ColumnBytesDownloadedSoFar));
            long total = row.GetLong(row.GetColumnIndex(DownloadManager.ColumnTotalSizeBytes));
            if (total > 0)
            {
                progress?.Report(done / (double)total);
            }

            if (status == DownloadStatus.Successful)
            {
                return target;
            }

            if (status == DownloadStatus.Failed)
            {
                return null;
            }

            await Task.Delay(500, ct);
        }

        manager.Remove(id);
        return null;
    }
}

public sealed class AndroidVideoPlayer : IVideoPlayer
{
    public Task PlayAsync(Uri source, string title)
    {
        Context ctx = AndroidApp.Context;
        Intent intent = new Intent(ctx, typeof(VideoPlayerActivity)).PutExtra("src", source.ToString()).PutExtra("title", title).AddFlags(ActivityFlags.NewTask);
        ctx.StartActivity(intent);
        return Task.CompletedTask;
    }
}

/// <summary>Item 8: a native VideoView screen; leaving it (Home, a notification) shrinks it into picture-in-picture.</summary>
[Activity(Theme = "@style/Maui.MainTheme.NoActionBar", SupportsPictureInPicture = true, ResizeableActivity = true, LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.Orientation)]
public sealed class VideoPlayerActivity : Activity
{
    private VideoView? _video;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _video = new VideoView(this);
        FrameLayout frame = new(this);
        frame.SetBackgroundColor(Android.Graphics.Color.Black);
        frame.AddView(_video, new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MatchParent, FrameLayout.LayoutParams.WrapContent, Android.Views.GravityFlags.Center));
        SetContentView(frame);
        MediaController controls = new(this);
        controls.SetAnchorView(_video);
        _video.SetMediaController(controls);
        _video.SetVideoURI(AUri.Parse(Intent?.GetStringExtra("src")));
        _video.Prepared += (_, _) => _video.Start();
        Title = Intent?.GetStringExtra("title");
    }

    protected override void OnUserLeaveHint()
    {
        base.OnUserLeaveHint();
        if (_video?.IsPlaying == true)
        {
            EnterPictureInPictureMode(new PictureInPictureParams.Builder().SetAspectRatio(new Android.Util.Rational(16, 9))!.Build()!);
        }
    }
}

/// <summary>HEIC (and anything over 10 MB or 4096 px) re-encoded as JPEG, the way the API accepts pictures.</summary>
public sealed class AndroidImagePrep : IImagePrep
{
    private const long MaxBytes = 10L * 1024 * 1024;
    private const int MaxSide = 4096;

    public async Task<string> PrepareAsync(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        bool accepted = ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif";
        if (accepted && new FileInfo(path).Length <= MaxBytes)
        {
            return path;
        }

        using Bitmap? full = ImageDecoder.DecodeBitmap(ImageDecoder.CreateSource(new Java.IO.File(path)));
        if (full is null)
        {
            return path;
        }

        double scale = Math.Min(1, MaxSide / (double)Math.Max(full.Width, full.Height));
        using Bitmap sized = scale < 1 ? Bitmap.CreateScaledBitmap(full, (int)(full.Width * scale), (int)(full.Height * scale), true)! : full.Copy(Bitmap.Config.Argb8888!, false)!;
        string target = System.IO.Path.Combine(FileSystem.CacheDirectory, $"{System.IO.Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}.jpg");
        await using FileStream file = File.Create(target);
        await sized.CompressAsync(Bitmap.CompressFormat.Jpeg!, 88, file);
        return target;
    }
}
