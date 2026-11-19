using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Mobile.Services;

/// <summary>
/// Signs every call: the Entra bearer token when signed in with Microsoft (the API skips antiforgery for bearer
/// callers), otherwise the session cookie from /dev-login (Debug) — and then writes carry the X-CSRF-TOKEN header the
/// API's route groups require, refreshed once on an antiforgery 400 exactly as the web client's handler does.
/// </summary>
public sealed class AuthDelegatingHandler(SecureSessionStore sessionStore) : DelegatingHandler
{
    private static string? s_csrf;

    public static void ForgetCsrf() => s_csrf = null;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? token = await sessionStore.GetBearerTokenAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        bool write = request.Method != HttpMethod.Get && request.Method != HttpMethod.Head;
        if (!write)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false); // so a retry can resend it
        }

        await SetCsrfAsync(request, refresh: false, cancellationToken).ConfigureAwait(false);
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.BadRequest || !(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Contains("antiforgery", StringComparison.OrdinalIgnoreCase))
        {
            return response;
        }

        response.Dispose();
        using HttpRequestMessage retry = new(request.Method, request.RequestUri) { Content = request.Content };
        await SetCsrfAsync(retry, refresh: true, cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetCsrfAsync(HttpRequestMessage request, bool refresh, CancellationToken ct)
    {
        if (refresh || s_csrf is null)
        {
            Uri tokenUri = new(request.RequestUri!, "/api/antiforgery/token");
            using HttpRequestMessage get = new(HttpMethod.Get, tokenUri);
            using HttpResponseMessage got = await base.SendAsync(get, ct).ConfigureAwait(false);
            s_csrf = got.IsSuccessStatusCode ? (await got.Content.ReadFromJsonAsync(MobileJson.Default.AntiforgeryToken, ct).ConfigureAwait(false))?.Token : null;
        }

        request.Headers.Remove("X-CSRF-TOKEN");
        if (s_csrf is not null)
        {
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", s_csrf);
        }
    }
}
