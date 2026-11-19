using PoRedoMedia.Mobile.Services;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Mobile.Pages;

/// <summary>Signed in = the quota answers.</summary>
public sealed class Session(ApiClient api, SecureSessionStore tokens)
{
    public QuotaStatusDto? Quota { get; private set; }

    public async Task<bool> RefreshAsync()
    {
        Quota = await api.GetQuotaAsync();
        return Quota is not null || (!ApiClient.Online && !string.IsNullOrEmpty(await tokens.GetBearerTokenAsync()));
    }
}

public sealed class LoginPage(ApiClient api, Session session, EntraAuthService entra, MobileConfig config, CookieJar cookies) : ContentPage
{
    private readonly Label _status = Ui.Muted(null);

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Title = "Sign in";
        Shell.SetTabBarIsVisible(this, false);
        Entry email = new() { Placeholder = "dev@example.com", Text = "dev@example.com", Keyboard = Keyboard.Email };
        VerticalStackLayout body = Ui.Stack(14, Ui.H1("PoRedoMedia"), Ui.Text("Remix photos and videos with AI."), Ui.Primary("Continue with Microsoft", MicrosoftAsync));
        if (MobileConfig.DevLoginAllowed)
        {
            body.Add(email);
            body.Add(Ui.Secondary("Dev sign-in", async () =>
            {
                if (!await api.DevLoginAsync(email.Text ?? "dev@example.com"))
                {
                    _status.Text = $"Dev sign-in failed — is the API at {config.BaseUrl} running in Development?";
                    return;
                }

                AuthDelegatingHandler.ForgetCsrf();
                await cookies.SaveAsync();
                await FinishAsync();
            }));
        }

        body.Add(_status);
        Content = new ScrollView { Content = body, Padding = 20 };
    }

    private async Task MicrosoftAsync()
    {
        if (string.IsNullOrWhiteSpace(config.EntraClientId))
        {
            _status.Text = "Microsoft sign-in needs the app's Entra client id — set it under Settings → Server.";
            return;
        }

        try
        {
            cookies.Clear();
            await entra.SignInWithPkceAsync();
            await FinishAsync();
        }
        catch (TaskCanceledException)
        {
            _status.Text = "Sign-in was cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Sign-in failed: {ex.Message}";
        }
    }

    private async Task FinishAsync()
    {
        if (await session.RefreshAsync() && session.Quota is not null)
        {
            await Shell.Current.GoToAsync("..");
        }
        else
        {
            _status.Text = "Signed in, but the server didn't accept the session (is your email on Auth:AllowedEmails?).";
        }
    }
}

public sealed class SettingsPage(ApiClient api, Session session, MobileConfig config, EntraAuthService entra, CookieJar cookies, IBackgroundWork background) : ContentPage
{
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Title = "Settings";
        VerticalStackLayout body = Ui.Stack(10, Ui.H1("Settings"));
        Content = new ScrollView { Content = body, Padding = 16 };
        bool signedIn = await session.RefreshAsync() && session.Quota is not null;
        VerticalStackLayout account = Ui.Stack(6, Ui.H2("Account"));
        if (signedIn && session.Quota is { } q)
        {
            account.Add(Ui.Text(q.Unlimited ? "Unlimited runs" : $"{q.Used} of {q.Limit} runs used today (resets {q.ResetsAt.LocalDateTime:t})"));
            account.Add(Ui.Secondary("Sign out", async () =>
            {
                await api.LogoutAsync();
                await entra.SignOutAsync();
                cookies.Clear();
                AuthDelegatingHandler.ForgetCsrf();
                await Shell.Current.GoToAsync("login");
            }));
        }
        else
        {
            account.Add(Ui.Primary("Sign in", () => Shell.Current.GoToAsync("login")));
        }

        body.Add(Ui.Card(account));
        body.Add(Ui.Card(Ui.Stack(6,
            Ui.H2("This phone"),
            Ui.Muted("Uploads continue in the background and resume after a dropped connection; a render's progress stays on a notification, and you're notified when it's ready."),
            Ui.Secondary("Allow notifications", async () => await Ui.Toast(await background.RequestNotificationsAsync() ? "Notifications are on." : "Turn notifications on in the phone's Settings.")))));

        Entry baseUrl = new() { Text = config.BaseUrl, Keyboard = Keyboard.Url };
        Entry clientId = new() { Text = config.EntraClientId, Placeholder = "Entra app client id" };
        body.Add(Ui.Card(Ui.Stack(6,
            Ui.H2("Server"),
            Ui.Muted($"Microsoft sign-in needs the redirect {MobileConfig.RedirectUri} on the app registration. Debug: scripts/mobile-dev.ps1 (maps the API and Azurite's blob port)."),
            baseUrl, clientId,
            Ui.Secondary("Save", () =>
            {
                config.BaseUrl = baseUrl.Text ?? string.Empty;
                config.EntraClientId = clientId.Text ?? string.Empty;
                AuthDelegatingHandler.ForgetCsrf();
                return Ui.Toast("Saved.");
            }))));
        body.Add(Ui.Muted($"v{AppInfo.Current.VersionString}"));
    }
}
