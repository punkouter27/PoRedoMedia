using PoRedoMedia.Mobile.Services;

namespace PoRedoMedia.Mobile.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly MobileConfig _config;
    private readonly SecureSessionStore _store;
    private readonly EntraAuthService _entraAuth;
    private readonly PoRedoMediaMobileClient _client;

    public SettingsPage(
        MobileConfig config,
        SecureSessionStore store,
        EntraAuthService entraAuth,
        PoRedoMediaMobileClient client)
    {
        _config = config;
        _store = store;
        _entraAuth = entraAuth;
        _client = client;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        BaseUrlEntry.Text = _config.BaseUrl;
        ClientIdEntry.Text = _config.EntraClientId;
        TenantIdEntry.Text = _config.EntraTenantId;
        ScopeEntry.Text = _config.EntraScope;
        RedirectLabel.Text = $"Redirect URI: {MobileConfig.RedirectUri}";
        FakeUserEntry.Text = await _store.GetFakeUserAsync();
        TokenEntry.Text = await _store.GetBearerTokenAsync();
    }

    private void SaveConfigFields()
    {
        _config.BaseUrl = BaseUrlEntry.Text;
        _config.EntraClientId = ClientIdEntry.Text;
        _config.EntraTenantId = TenantIdEntry.Text;
        _config.EntraScope = ScopeEntry.Text;
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        SaveConfigFields();
        await _store.SetFakeUserAsync(FakeUserEntry.Text);
        await _store.SetBearerTokenAsync(TokenEntry.Text);
        ResultLabel.Text = $"Saved settings for {_config.BaseUrl}";
    }

    private async void OnEntraSignInClicked(object? sender, EventArgs e)
    {
        try
        {
            SaveConfigFields();
            await _entraAuth.SignInWithPkceAsync();
            TokenEntry.Text = await _store.GetBearerTokenAsync();
            ResultLabel.Text = "Signed in with Microsoft Entra ID (token stored in SecureStorage).";
        }
        catch (Exception ex)
        {
            ResultLabel.Text = $"Sign-in error: {ex.Message}";
        }
    }

    private async void OnSignOutClicked(object? sender, EventArgs e)
    {
        await _entraAuth.SignOutAsync();
        TokenEntry.Text = string.Empty;
        ResultLabel.Text = "Cleared Bearer token from SecureStorage.";
    }

    private async void OnTestClicked(object? sender, EventArgs e)
    {
        try
        {
            var quota = await _client.GetQuotaAsync();
            ResultLabel.Text = quota is not null ? $"Connected! Remaining runs: {(quota.Unlimited ? "Unlimited" : quota.Remaining)}" : "Connected, no quota data.";
        }
        catch (Exception ex)
        {
            ResultLabel.Text = $"Error: {ex.Message}";
        }
    }
}
