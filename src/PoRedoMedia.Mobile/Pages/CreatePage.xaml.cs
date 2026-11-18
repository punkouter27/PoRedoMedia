using PoRedoMedia.Mobile.Services;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Mobile.Pages;

public partial class CreatePage : ContentPage
{
    private readonly PoRedoMediaMobileClient _client;
    private readonly OfflineMediaQueue _queue;

    public CreatePage(PoRedoMediaMobileClient client, OfflineMediaQueue queue)
    {
        _client = client;
        _queue = queue;
        InitializeComponent();
        FunctionPicker.SelectedIndex = 0;
        _client.RunProgressed += OnRunProgressed;
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        QueueLabel.Text = $"Queued uploads: {(await _queue.ListAsync()).Count}";
    }

    private void OnRunProgressed(RunProgressDto progress)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            RunStatusLabel.Text = $"Run {progress.RunId}: {progress.Status} ({progress.Percent}% · {progress.Step?.ToString() ?? progress.Message})";
            if (progress.Status is RunStatus.Complete or RunStatus.Failed && HapticFeedback.Default.IsSupported)
            {
                HapticFeedback.Default.Perform(HapticFeedbackType.LongPress);
            }
        });
    }

    private async void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        if (e.NetworkAccess == NetworkAccess.Internet)
        {
            await FlushQueueAsync();
        }
    }

    private MediaFunction SelectedFunction()
    {
        var name = FunctionPicker.SelectedItem as string;
        return Enum.TryParse<MediaFunction>(name, out var fn) ? fn : MediaFunction.Restyle;
    }

    private async void OnCapturePhotoClicked(object? sender, EventArgs e)
    {
        if (!MediaPicker.Default.IsCaptureSupported)
        {
            StatusLabel.Text = "Camera capture is unavailable on this device.";
            return;
        }

        await HandleMediaAsync(await MediaPicker.Default.CapturePhotoAsync(), isImage: true);
    }

    private async void OnPickPhotoClicked(object? sender, EventArgs e)
    {
        var files = await MediaPicker.Default.PickPhotosAsync();
        await HandleMediaAsync(files.FirstOrDefault(), isImage: true);
    }

    private async void OnPickVideoClicked(object? sender, EventArgs e)
    {
        var files = await MediaPicker.Default.PickVideosAsync();
        await HandleMediaAsync(files.FirstOrDefault(), isImage: false);
    }

    private async Task HandleMediaAsync(FileResult? file, bool isImage)
    {
        if (file is null)
        {
            return;
        }

        if (isImage)
        {
            PreviewImage.Source = ImageSource.FromFile(file.FullPath);
            PreviewImage.IsVisible = true;
        }

        var fn = SelectedFunction();
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            await _queue.EnqueueAsync(file.FullPath, file.FileName, fn);
            QueueLabel.Text = $"Queued uploads: {(await _queue.ListAsync()).Count}";
            StatusLabel.Text = $"Offline — queued {file.FileName}.";
            return;
        }

        try
        {
            StatusLabel.Text = $"Uploading {file.FileName} to SAS Blob...";
            var run = await _client.UploadAndRunAsync(file.FullPath, file.FileName, fn);
            RunStatusLabel.Text = $"Run {run.Id}: {run.Status}";
            await _client.WatchRunAsync(run.Id);
        }
        catch (Exception ex)
        {
            await _queue.EnqueueAsync(file.FullPath, file.FileName, fn);
            QueueLabel.Text = $"Queued uploads: {(await _queue.ListAsync()).Count}";
            StatusLabel.Text = $"Upload queued after error: {ex.Message}";
        }
    }

    private async void OnFlushClicked(object? sender, EventArgs e) => await FlushQueueAsync();

    private async Task FlushQueueAsync()
    {
        var items = await _queue.ListAsync();
        var done = 0;
        foreach (var item in items)
        {
            try
            {
                var run = await _client.UploadAndRunAsync(item.LocalPath, item.FileName, item.Function);
                await _queue.RemoveAsync(item.Id);
                done++;
                await _client.WatchRunAsync(run.Id);
            }
            catch
            {
                break;
            }
        }

        var remaining = (await _queue.ListAsync()).Count;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            QueueLabel.Text = $"Queued uploads: {remaining}";
            if (done > 0)
            {
                StatusLabel.Text = $"Uploaded {done} queued item(s).";
            }
        });
    }
}
