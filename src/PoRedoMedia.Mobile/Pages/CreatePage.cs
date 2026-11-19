using PoRedoMedia.Mobile.Services;
using PoRedoMedia.Shared;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Mobile.Pages;

/// <summary>
/// Create: a photo or a video straight from the camera or the gallery (item 1), or a Library item; the functions that
/// fit it and their options; then the job goes to the background queue (upload → run) and this page is free again.
/// </summary>
public sealed class CreatePage(ApiClient api, Session session, Transfers transfers, IImagePrep imagePrep, IBackgroundWork background) : ContentPage, IQueryAttributable
{
    private readonly VerticalStackLayout _body = new() { Spacing = 10, Padding = 16 };
    private readonly HashSet<MediaFunction> _picked = [];
    private readonly Dictionary<string, string> _options = [];
    private AppConfigDto? _config;
    private string? _localPath;
    private Guid? _librarySource;
    private string _sourceLabel = string.Empty;
    private MediaKind? _kind;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("source", out object? s) && Guid.TryParse(s as string, out Guid id))
        {
            _librarySource = id;
            _localPath = null;
            _kind = query.TryGetValue("kind", out object? k) && Enum.TryParse(k as string, out MediaKind kind) ? kind : MediaKind.Image;
            _sourceLabel = query.TryGetValue("title", out object? t) ? t as string ?? "Library item" : "Library item";
            _picked.Clear();
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Title = "Create";
        Content = new ScrollView { Content = _body };
        if (!await session.RefreshAsync())
        {
            await Shell.Current.GoToAsync("login");
            return;
        }

        _config ??= await api.GetConfigAsync();
        Render();
    }

    private void Render()
    {
        _body.Clear();
        _body.Add(Ui.H1("Remix something"));
        _body.Add(Ui.Row(Ui.Secondary("📷 Take photo", () => PickAsync(photo: true, capture: true)), Ui.Secondary("🖼 Choose photo", () => PickAsync(photo: true, capture: false))));
        _body.Add(Ui.Row(Ui.Secondary("🎥 Record video", () => PickAsync(photo: false, capture: true)), Ui.Secondary("🎞 Choose video", () => PickAsync(photo: false, capture: false))));
        if (_kind is not { } kind)
        {
            _body.Add(Ui.Muted("Pictures up to 10 MB (iPhone HEIC is converted), videos up to 200 MB and a minute long. Or open something from the Library and tap Remix."));
            return;
        }

        _body.Add(Ui.Card(Ui.Stack(4, Ui.H2(kind == MediaKind.Video ? "Video" : "Picture"), Ui.Muted(_sourceLabel))));
        if (_localPath is not null && kind == MediaKind.Image)
        {
            _body.Add(new Image { Source = ImageSource.FromFile(_localPath), HeightRequest = 220, Aspect = Aspect.AspectFit });
        }

        MediaFunction[] available = _config?.AvailableFunctions ?? [];
        _body.Add(Ui.H2("What should happen?"));
        foreach (MediaFunction function in FunctionStack.For(kind).Where(available.Contains))
        {
            bool on = _picked.Contains(function);
            CheckBox box = new() { IsChecked = on, IsEnabled = on || FunctionStack.CanAdd(_picked, function) };
            box.CheckedChanged += (_, e) =>
            {
                if (e.Value)
                {
                    _picked.Add(function);
                }
                else
                {
                    _picked.Remove(function);
                }

                Render();
            };
            Grid row = new() { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)] };
            row.Add(box);
            row.Add(Ui.Stack(0, new Label { Text = FunctionStack.Label(function), FontAttributes = FontAttributes.Bold }, Ui.Muted(FunctionStack.Describe(function))), 1);
            _body.Add(row);
        }

        if (available.Length == 0)
        {
            _body.Add(Ui.Colored("The server has no functions configured right now.", Ui.Warn));
        }

        AddOptions();
        Label error = Ui.Colored(null, Ui.Bad, 14);
        _body.Add(error);
        _body.Add(Ui.Primary("Start", async () =>
        {
            string? problem = _picked.Count == 0 ? "Pick at least one function." : FunctionStack.Validate(kind, _picked) ?? FunctionStack.MissingOption(_picked, _options);
            if (problem is not null)
            {
                error.Text = problem;
                return;
            }

            await background.RequestNotificationsAsync();
            FileInfo? file = _localPath is null ? null : new FileInfo(_localPath);
            await transfers.AddAsync(new TransferJob
            {
                LocalPath = _localPath ?? string.Empty,
                FileName = file?.Name ?? string.Empty,
                Size = file?.Length ?? 0,
                SourceId = _librarySource,
                Functions = [.. FunctionStack.InRunOrder(_picked)],
                Options = new Dictionary<string, string>(_options.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))),
            });
            _kind = null;
            _localPath = null;
            _librarySource = null;
            _picked.Clear();
            _options.Clear();
            await Shell.Current.GoToAsync("//activity");
        }));
    }

    /// <summary>Only the options the picked functions need, with the same rules the server checks.</summary>
    private void AddOptions()
    {
        if (_picked.Contains(MediaFunction.Restyle))
        {
            List<StyleRecipe> styles = [.. StyleRecipeCatalog.All];
            Picker style = new() { Title = "Style", ItemsSource = styles.Select(s => $"{s.Title} · {s.Category}").ToList(), SelectedIndex = styles.FindIndex(s => s.Id == _options.GetValueOrDefault(RunOptions.RestyleStyle)) };
            style.SelectedIndexChanged += (_, _) => _options[RunOptions.RestyleStyle] = style.SelectedIndex >= 0 ? styles[style.SelectedIndex].Id : string.Empty;
            _body.Add(Ui.Field("Restyle as", style));
            _body.Add(Bound(RunOptions.RestylePrompt, "Or describe a look (optional)"));
        }

        if (_picked.Contains(MediaFunction.MemeCaption))
        {
            string[] modes = [RunOptions.MemeModeAi, RunOptions.MemeModeText, RunOptions.MemeModeTemplate];
            Picker mode = new() { Title = "Meme text", ItemsSource = new[] { "AI writes it", "I'll type it", "A classic template" }, SelectedIndex = Math.Max(0, Array.IndexOf(modes, _options.GetValueOrDefault(RunOptions.MemeMode, RunOptions.MemeModeAi))) };
            mode.SelectedIndexChanged += (_, _) => { _options[RunOptions.MemeMode] = modes[Math.Max(0, mode.SelectedIndex)]; Render(); };
            _body.Add(Ui.Field("Meme text", mode));
            if (_options.GetValueOrDefault(RunOptions.MemeMode) == RunOptions.MemeModeText)
            {
                _body.Add(Bound(RunOptions.MemeTop, "Top text"));
                _body.Add(Bound(RunOptions.MemeBottom, "Bottom text"));
            }
            else if (_options.GetValueOrDefault(RunOptions.MemeMode) == RunOptions.MemeModeTemplate)
            {
                Picker template = new() { Title = "Template" };
                _body.Add(template);
                _ = Task.Run(async () =>
                {
                    List<MemeTemplateDto> all = await api.MemeTemplatesAsync() ?? [];
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        template.ItemsSource = all.Select(t => t.Name).ToList();
                        template.SelectedIndex = all.FindIndex(t => t.Id == _options.GetValueOrDefault(RunOptions.MemeTemplate));
                        template.SelectedIndexChanged += (_, _) => _options[RunOptions.MemeTemplate] = template.SelectedIndex >= 0 ? all[template.SelectedIndex].Id : string.Empty;
                    });
                });
            }
        }

        if (_picked.Contains(MediaFunction.PhotoToVideo))
        {
            _body.Add(Bound(RunOptions.VideoPrompt, "What should happen in the video?"));
        }

        if (_picked.Contains(MediaFunction.VideoRoast) && _config?.RoastVoices is { Length: > 0 } voices)
        {
            Picker voice = new() { Title = "Roast voice", ItemsSource = voices, SelectedIndex = Math.Max(0, Array.IndexOf(voices, _options.GetValueOrDefault(RunOptions.VideoRoastVoice))) };
            voice.SelectedIndexChanged += (_, _) => _options[RunOptions.VideoRoastVoice] = voices[Math.Max(0, voice.SelectedIndex)];
            _body.Add(Ui.Field("Voice", voice));
            _body.Add(Ui.Field("Crude", Ui.Toggle(_options.GetValueOrDefault(RunOptions.VideoRoastCrude) == "true", on => { _options[RunOptions.VideoRoastCrude] = on ? "true" : "false"; return Task.CompletedTask; })));
        }
    }

    private Entry Bound(string key, string placeholder)
    {
        Entry entry = new() { Placeholder = placeholder, Text = _options.GetValueOrDefault(key) };
        entry.TextChanged += (_, e) => _options[key] = e.NewTextValue ?? string.Empty;
        return entry;
    }

    private async Task PickAsync(bool photo, bool capture)
    {
        try
        {
            FileResult? picked = (photo, capture) switch
            {
                (true, true) => MediaPicker.Default.IsCaptureSupported ? await MediaPicker.Default.CapturePhotoAsync() : null,
                (true, false) => (await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { SelectionLimit = 1 })).FirstOrDefault(),
                (false, true) => MediaPicker.Default.IsCaptureSupported ? await MediaPicker.Default.CaptureVideoAsync() : null,
                _ => (await MediaPicker.Default.PickVideosAsync(new MediaPickerOptions { SelectionLimit = 1 })).FirstOrDefault(),
            };
            if (picked is null)
            {
                return;
            }

            // Into the app's own storage, so the background upload still has it after the picker's temp copy is gone.
            string copy = Path.Combine(FileSystem.CacheDirectory, $"pick-{Guid.NewGuid():N}{Path.GetExtension(picked.FileName)}");
            await using (Stream source = await picked.OpenReadAsync())
            await using (FileStream target = File.Create(copy))
            {
                await source.CopyToAsync(target);
            }

            _localPath = photo ? await imagePrep.PrepareAsync(copy) : copy;
            _kind = photo ? MediaKind.Image : MediaKind.Video;
            _librarySource = null;
            _sourceLabel = $"{Path.GetFileName(_localPath)} · {new FileInfo(_localPath).Length / 1024.0 / 1024.0:F1} MB";
            _picked.Clear();
            Render();
        }
        catch (Exception ex) when (ex is PermissionException or FeatureNotSupportedException)
        {
            await Ui.Toast("PoRedoMedia needs the camera / photos permission for that.");
        }
    }
}
