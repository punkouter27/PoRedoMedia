namespace PoRedoMedia.Mobile.Services;

public sealed class MobileConfig
{
    private const string BaseUrlKey = "poredomedia_api_base_url";
    private const string ClientIdKey = "poredomedia_entra_client_id";
    private const string TenantIdKey = "poredomedia_entra_tenant_id";
    private const string ScopeKey = "poredomedia_entra_scope";
    private readonly IPreferences _preferences = Preferences.Default;

    public string BaseUrl
    {
        get => _preferences.Get(BaseUrlKey, GetDefaultBaseUrl());
        set => _preferences.Set(BaseUrlKey, NormalizeUrl(value));
    }

    public string EntraClientId
    {
        get => _preferences.Get(ClientIdKey, string.Empty);
        set => _preferences.Set(ClientIdKey, (value ?? string.Empty).Trim());
    }

    public string EntraTenantId
    {
        get => _preferences.Get(TenantIdKey, "common");
        set => _preferences.Set(TenantIdKey, string.IsNullOrWhiteSpace(value) ? "common" : value.Trim());
    }

    public string EntraScope
    {
        get => _preferences.Get(ScopeKey, "openid profile email");
        set => _preferences.Set(ScopeKey, string.IsNullOrWhiteSpace(value) ? "openid profile email" : value.Trim());
    }

    public const string ProductionHost = "app-poredomedia2.azurewebsites.net";

    /// <summary><c>poredomedia://auth</c> (sign-in), <c>poredomedia://media/{id}</c> (a finished render's notification).</summary>
    public const string AppScheme = "poredomedia";

    public static string RedirectUri => $"{AppScheme}://auth";

    /// <summary>Debug builds may use the API's /dev-login (Development/Test only).</summary>
    public static bool DevLoginAllowed =>
#if DEBUG
        true;
#else
        false;
#endif

    // Debug: scripts/mobile-dev.ps1 runs adb reverse for the API (device :5000 → PC :4100) and for Azurite's blob port
    // (10000), so the upload and download links the API hands out (127.0.0.1:10000) work from the phone too.
    public static string GetDefaultBaseUrl() =>
#if DEBUG
        DeviceInfo.Current.Platform == DevicePlatform.Android ? "http://localhost:5000" : "http://localhost:4100";
#else
        $"https://{ProductionHost}";
#endif

    private static string NormalizeUrl(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(trimmed) ? GetDefaultBaseUrl() : trimmed;
    }
}
