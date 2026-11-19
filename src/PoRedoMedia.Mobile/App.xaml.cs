using PoRedoMedia.Mobile.Services;

namespace PoRedoMedia.Mobile;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
        => new(new AppShell());

    /// <summary>poredomedia://media/{id} — a "your remix is ready" notification opens the result.</summary>
    protected override void OnAppLinkRequestReceived(Uri uri)
    {
        base.OnAppLinkRequestReceived(uri);
        if (uri.Scheme != MobileConfig.AppScheme || uri.Host != "media" || !Guid.TryParse(uri.AbsolutePath.Trim('/'), out Guid id))
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            for (int i = 0; Shell.Current is null && i < 50; i++)
            {
                await Task.Delay(100);
            }

            if (Shell.Current is { } shell)
            {
                await shell.GoToAsync($"//library/media?id={id}");
            }
        });
    }
}
