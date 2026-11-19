using PoRedoMedia.Mobile.Services;
using PoRedoMedia.Shared;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Mobile.Pages;

/// <summary>Activity: the phone's queue (uploading, starting, rendering — with progress) and the server's recent runs.</summary>
public sealed class ActivityPage(ApiClient api, Transfers transfers) : ContentPage
{
    private readonly VerticalStackLayout _body = new() { Spacing = 10, Padding = 16 };

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Title = "Activity";
        Content = new RefreshView { Content = new ScrollView { Content = _body }, Command = new Command(async () => { await transfers.PumpAsync(); await RenderAsync(); }) };
        transfers.Changed += OnChanged;
        _ = transfers.PumpAsync();
        await RenderAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        transfers.Changed -= OnChanged;
    }

    private void OnChanged() => MainThread.BeginInvokeOnMainThread(() => _ = RenderAsync());

    private async Task RenderAsync()
    {
        _body.Clear();
        _body.Add(Ui.H1("Activity"));
        foreach (TransferJob job in transfers.Jobs.OrderByDescending(j => j.CreatedAt))
        {
            VerticalStackLayout card = Ui.Stack(4,
                new Label { Text = string.Join(" + ", job.Functions.Select(FunctionStack.Label)), FontAttributes = FontAttributes.Bold },
                Ui.Muted($"{job.State} · {job.Message}"));
            if (job.State is not (TransferState.Done or TransferState.Failed))
            {
                card.Add(new ProgressBar { Progress = job.Percent / 100.0, ProgressColor = Ui.Accent });
            }

            Color stripe = job.State == TransferState.Failed ? Ui.Bad : job.State == TransferState.Done ? Ui.Good : Ui.Accent;
            _body.Add(Ui.Card(card, job.FirstOutput is { } output ? () => Shell.Current.GoToAsync($"media?id={output}") : null, stripe));
        }

        if (transfers.Jobs.Any(j => j.State is TransferState.Done or TransferState.Failed))
        {
            _body.Add(Ui.Secondary("Clear finished", async () => { await transfers.ClearFinishedAsync(); await RenderAsync(); }));
        }

        _body.Add(Ui.H2("Recent runs"));
        foreach (RunDto run in (await api.ListRunsAsync() ?? []).OrderByDescending(r => r.CreatedAt).Take(20))
        {
            Guid? first = run.OutputIds.Length > 0 ? run.OutputIds[0] : null;
            _body.Add(Ui.Card(Ui.Stack(2,
                new Label { Text = string.Join(" + ", run.Functions.Select(FunctionStack.Label)) },
                Ui.Muted($"{run.Status} · {run.CreatedAt.LocalDateTime:g}{(run.Error is { } e ? $" · {e}" : string.Empty)}")),
                first is { } id ? () => Shell.Current.GoToAsync($"media?id={id}") : null));
        }
    }
}

/// <summary>Library: everything made or uploaded, newest first.</summary>
public sealed class LibraryPage(ApiClient api) : ContentPage
{
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Title = "Library";
        VerticalStackLayout body = Ui.Stack(10, Ui.H1("Library"));
        Content = new RefreshView { Content = new ScrollView { Content = body, Padding = 16 }, Command = new Command(OnAppearing) };
        if (await api.GetStorageAsync() is { } storage)
        {
            body.Add(Ui.Muted($"{storage.Items} items · {storage.UsedBytes / 1048576.0:F0} of {storage.LimitBytes / 1048576.0:F0} MB"));
        }

        List<MediaDto> items = await api.ListMediaAsync() ?? [];
        if (items.Count == 0)
        {
            body.Add(Ui.Muted(ApiClient.Online ? "Nothing yet — make something on Create." : "You're offline."));
        }

        FlexLayout grid = new() { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        body.Add(grid);
        foreach (MediaDto item in items.OrderByDescending(m => m.Pinned).ThenByDescending(m => m.CreatedAt))
        {
            Image thumb = new() { HeightRequest = 110, WidthRequest = 110, Aspect = Aspect.AspectFill, BackgroundColor = Colors.Gray.WithAlpha(0.2f) };
            _ = Task.Run(async () =>
            {
                if (await api.BlobLinkAsync(item.Id, thumb: true) is { } link)
                {
                    MainThread.BeginInvokeOnMainThread(() => thumb.Source = ImageSource.FromUri(link));
                }
            });
            Border tile = Ui.Card(Ui.Stack(2, thumb, new Label { Text = $"{(item.Kind == MediaKind.Video ? "▶ " : string.Empty)}{item.Title}", FontSize = 11, LineBreakMode = LineBreakMode.TailTruncation, WidthRequest = 110 }), () => Shell.Current.GoToAsync($"media?id={item.Id}"));
            tile.Margin = new Thickness(0, 0, 8, 8);
            tile.Padding = 4;
            grid.Add(tile);
        }
    }
}

/// <summary>One item: watch (native player, picture-in-picture), save to Photos, download, share, remix, rename, delete.</summary>
public sealed class MediaPage(ApiClient api, IGallery gallery, IDownloader downloader, IVideoPlayer player) : ContentPage, IQueryAttributable
{
    private Guid _id;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out object? raw) && Guid.TryParse(raw as string ?? raw?.ToString(), out Guid id))
        {
            _id = id;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        VerticalStackLayout body = Ui.Stack(10);
        Content = new ScrollView { Content = body, Padding = 16 };
        if (await api.GetMediaAsync(_id) is not { } item)
        {
            body.Add(Ui.Muted("Not found (or still being made)."));
            return;
        }

        Title = item.Title;
        bool video = item.Kind == MediaKind.Video;
        Label status = Ui.Muted(null);
        body.Add(Ui.H1(item.Title));
        body.Add(Ui.Muted($"{item.Origin} · {item.CreatedAt.LocalDateTime:g} · {item.SizeBytes / 1048576.0:F1} MB{(item.DurationSeconds is { } d ? $" · {d:F0} s" : string.Empty)}"));
        if (item.Summary is { Length: > 0 } summary)
        {
            body.Add(Ui.Text(summary, 14));
        }

        Uri? link = await api.BlobLinkAsync(_id, thumb: video);
        if (link is not null)
        {
            body.Add(new Image { Source = ImageSource.FromUri(link), HeightRequest = 280, Aspect = Aspect.AspectFit });
        }

        if (item.Text is { Length: > 0 } text)
        {
            body.Add(Ui.Card(Ui.Text(text, 14)));
        }

        if (video)
        {
            body.Add(Ui.Primary("▶ Play (picture-in-picture when you leave)", async () =>
            {
                string? local = LocalCopy(item);
                Uri? source = local is not null ? new Uri(local) : await api.BlobLinkAsync(_id);
                if (source is null)
                {
                    await Ui.Toast("Can't reach the video.");
                    return;
                }

                await player.PlayAsync(source, item.Title);
            }));
        }

        body.Add(Ui.Secondary("Save to Photos", async () =>
        {
            if (await EnsureLocalAsync(item, status) is { } path)
            {
                status.Text = await gallery.SaveAsync(path, video) ?? "Saved to Photos.";
            }
        }));
        body.Add(Ui.Secondary(LocalCopy(item) is null ? "Download for offline" : "Downloaded ✓ (plays offline)", async () => await EnsureLocalAsync(item, status)));
        body.Add(Ui.Secondary("Share the file", async () =>
        {
            if (await EnsureLocalAsync(item, status) is { } path)
            {
                await Share.Default.RequestAsync(new ShareFileRequest(item.Title, new ShareFile(path, item.ContentType)));
            }
        }));
        body.Add(Ui.Secondary("Share a link", async () =>
        {
            if (await api.ShareAsync(_id) is { } url)
            {
                await Share.Default.RequestAsync(new ShareTextRequest { Title = item.Title, Uri = url.StartsWith("http", StringComparison.Ordinal) ? url : api.U(url).ToString() });
            }
        }));
        body.Add(Ui.Secondary("Remix this", () => Shell.Current.GoToAsync($"//create?source={_id}&kind={item.Kind}&title={Uri.EscapeDataString(item.Title)}")));

        Entry rename = new() { Text = item.Title };
        body.Add(Ui.Row(rename, Ui.Secondary("Rename", async () => status.Text = await api.UpdateMediaAsync(_id, rename.Text, null) ? "Renamed." : "Didn't save.")));
        body.Add(Ui.Field("Pinned", Ui.Toggle(item.Pinned, async on => await api.UpdateMediaAsync(_id, null, on))));
        body.Add(Ui.Danger("Delete", async () =>
        {
            if (await DisplayAlertAsync("Delete", $"Delete \"{item.Title}\"?", "Delete", "Keep"))
            {
                await api.DeleteMediaAsync(_id);
                await Shell.Current.GoToAsync("..");
            }
        }));
        body.Add(status);
    }

    private static string FileNameOf(MediaDto item) => $"{item.Id:N}{(item.Kind == MediaKind.Video ? ".mp4" : item.ContentType switch { "image/png" => ".png", "image/webp" => ".webp", "image/gif" => ".gif", _ => ".jpg" })}";

    private static string? LocalCopy(MediaDto item) =>
        Directory.EnumerateFiles(FileSystem.AppDataDirectory, FileNameOf(item), SearchOption.AllDirectories).FirstOrDefault()
        ?? (Path.Combine(FileSystem.CacheDirectory, FileNameOf(item)) is var cached && File.Exists(cached) ? cached : null);

    /// <summary>Item 5: the file on the phone, downloaded in the background by the system when it isn't yet.</summary>
    private async Task<string?> EnsureLocalAsync(MediaDto item, Label status)
    {
        if (LocalCopy(item) is { } have)
        {
            return have;
        }

        if (await api.BlobLinkAsync(item.Id) is not { } link)
        {
            status.Text = "Can't reach the file.";
            return null;
        }

        status.Text = "Downloading…";
        string? path = await downloader.DownloadAsync(link, FileNameOf(item), new Progress<double>(p => status.Text = $"Downloading {p:P0}"), CancellationToken.None);
        if (path is not null && !path.StartsWith(FileSystem.AppDataDirectory, StringComparison.Ordinal))
        {
            string kept = Path.Combine(FileSystem.CacheDirectory, FileNameOf(item));
            File.Copy(path, kept, overwrite: true);
            path = kept;
        }

        status.Text = path is null ? "The download failed." : "Downloaded.";
        return path;
    }
}
