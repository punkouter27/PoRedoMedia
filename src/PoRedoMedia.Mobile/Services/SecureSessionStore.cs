namespace PoRedoMedia.Mobile.Services;

public sealed class SecureSessionStore
{
    private const string BearerTokenKey = "poredomedia_bearer_token";
    private const string FakeUserKey = "poredomedia_dev_fake_user";
    private readonly ISecureStorage _storage = SecureStorage.Default;

    public async Task<string?> GetBearerTokenAsync()
        => await _storage.GetAsync(BearerTokenKey).ConfigureAwait(false);

    public async Task SetBearerTokenAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            _storage.Remove(BearerTokenKey);
            return;
        }

        await _storage.SetAsync(BearerTokenKey, token.Trim()).ConfigureAwait(false);
    }

    public async Task<string?> GetFakeUserAsync()
        => await _storage.GetAsync(FakeUserKey).ConfigureAwait(false) ?? "mobile-creator";

    public async Task SetFakeUserAsync(string? user)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            _storage.Remove(FakeUserKey);
            return;
        }

        await _storage.SetAsync(FakeUserKey, user.Trim()).ConfigureAwait(false);
    }
}
