using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace PoRedoMedia.E2EUI;

/// <summary>
/// One headless browser per test class, plus automatic listeners failing any E2EUI run on
/// browser console errors, unhandled page exceptions, failed static/API requests, or visible
/// #blazor-error-ui banners.
/// </summary>
public abstract class UiTestBase : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private readonly List<IPage> _trackedPages = [];
    private readonly ConcurrentQueue<string> _browserErrors = new();
    private readonly ConcurrentQueue<Func<string, bool>> _allowedErrorFilters = new();

    protected IBrowser Browser { get; private set; } = null!;
    protected static string BaseUrl => LiveServerFactAttribute.BaseUrl;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        // Fake camera and mic so capture flows work headless.
        Browser = await _playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            Args = ["--use-fake-ui-for-media-stream", "--use-fake-device-for-media-stream"],
        });
    }

    public async Task DisposeAsync()
    {
        try
        {
            foreach (var page in _trackedPages)
            {
                try
                {
                    if (!page.IsClosed && await page.Locator("#blazor-error-ui").IsVisibleAsync())
                        _browserErrors.Enqueue($"{page.Url}: #blazor-error-ui became visible.");
                }
                catch (PlaywrightException)
                {
                    // Page already navigated away or closed by the test.
                }
            }
        }
        finally
        {
            await Browser.DisposeAsync();
            _playwright.Dispose();
        }

        var unexpected = _browserErrors.Where(err => !_allowedErrorFilters.Any(allow => allow(err))).ToList();
        if (unexpected.Count > 0)
            throw new InvalidOperationException("E2EUI detected browser console/network errors:\n - " + string.Join("\n - ", unexpected));
    }

    protected async Task<IPage> NewTrackedPageAsync()
    {
        var page = await Browser.NewPageAsync();
        TrackPage(page);
        return page;
    }

    protected void AllowError(Func<string, bool> predicate) => _allowedErrorFilters.Enqueue(predicate);

    protected void TrackPage(IPage page)
    {
        _trackedPages.Add(page);
        page.Console += (_, m) =>
        {
            if (m.Type == "error")
                _browserErrors.Enqueue($"Console error on {page.Url}: {m.Text}");
        };
        page.PageError += (_, err) => _browserErrors.Enqueue($"PageError on {page.Url}: {err}");
        page.Crash += (_, _) => _browserErrors.Enqueue($"Page crashed on {page.Url}");
        page.RequestFailed += (_, req) =>
        {
            // Ignore navigation cancellations triggered by the test closing/redirecting a page.
            if (!string.Equals(req.Failure, "net::ERR_ABORTED", StringComparison.Ordinal))
                _browserErrors.Enqueue($"RequestFailed {req.Method} {req.Url}: {req.Failure}");
        };
        page.Response += async (_, res) =>
        {
            if (res.Status >= 400 && !res.Url.EndsWith("/favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                var body = "";
                try { body = await res.TextAsync(); } catch (PlaywrightException) { }
                _browserErrors.Enqueue($"HTTP {res.Status} for {res.Request.Method} {res.Url} {(body.Length > 200 ? body[..200] : body)}".Trim());
            }
        };
    }

    /// <summary>A page signed in as a fresh developer user, so tests never see each other's media.</summary>
    /// <param name="switches">Settings -> Advanced switches to turn on first, by key (Client/Services/AppSettings).</param>
    protected async Task<IPage> SignedInPageAsync(string path, params string[] switches)
    {
        var page = await NewTrackedPageAsync();
        if (switches.Length > 0)
            await page.AddInitScriptAsync(string.Concat(switches.Select(key => $"localStorage.setItem('{key}', 'on');")));
        var user = Uri.EscapeDataString($"e2e-{Guid.NewGuid():N}@localhost");
        await page.GotoAsync($"{BaseUrl}/dev-login?email={user}&returnUrl={Uri.EscapeDataString(path)}");
        return page;
    }
}
