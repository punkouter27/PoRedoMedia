using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace PoRedoMedia.E2EUI;

/// <summary>
/// The shell every page shares (Layout/MainLayout, the one layout): Radzen's stylesheet first and once, no navigation
/// signed out, the exact email top-right signed in, one h1 and a footer stamp on every page, and no axe violations
/// at laptop and phone width, signed in and out.
/// </summary>
public sealed class ShellUiTests : UiTestBase
{
    private static readonly (int Width, int Height)[] Sizes = [(1366, 768), (390, 844)];

    [LiveServerFact]
    public async Task Radzen_css_is_the_first_stylesheet_loads_once_and_the_app_css_still_wins()
    {
        foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark })
        {
            var page = await NewTrackedPageAsync();
            await page.EmulateMediaAsync(new() { ColorScheme = scheme });
            await page.GotoAsync(BaseUrl + "/login");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "PoRedoMedia", Level = 1 })).ToBeVisibleAsync(new() { Timeout = 30_000 });

            var sheets = await page.EvaluateAsync<string[]>("""
                () => [...document.querySelectorAll('link[rel=stylesheet]')]
                    .map(l => (l.getAttribute('href') || '') + '|' + (matchMedia(l.media || 'all').matches ? 'on' : 'off'))
                """);
            var radzen = sheets.Select((s, i) => (s, i)).Where(x => x.s.Contains("_content/Radzen.Blazor/css/", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(radzen);
            // Radzen's theme files come first in <head>, before every app stylesheet.
            Assert.Equal(Enumerable.Range(0, radzen.Count), radzen.Select(x => x.i));
            // Exactly one of them applies (light or dark, by the device), and no <RadzenTheme> adds a second copy.
            Assert.Single(radzen, x => x.s.EndsWith("|on", StringComparison.Ordinal));
            Assert.Equal(0, await page.Locator("head link[href*='Radzen.Blazor/css']:not([media])").CountAsync());
            var imports = await page.EvaluateAsync<int>("""
                () => [...document.styleSheets].reduce((n, s) => {
                    try { return n + [...s.cssRules].filter(r => r instanceof CSSImportRule).length; } catch { return n; } }, 0)
                """);
            Assert.Equal(0, imports);
            var fetched = await page.EvaluateAsync<string[]>("() => performance.getEntriesByType('resource').map(e => e.name).filter(n => n.includes('Radzen.Blazor/css'))");
            Assert.Equal(fetched.Length, fetched.Distinct().Count());

            // The app's look wins over Radzen's: the button is the Po accent, the title the Po display font.
            var look = await page.EvaluateAsync<string[]>("""
                () => {
                    const probe = document.createElement('div');
                    probe.style.color = 'var(--po-accent)';
                    document.body.appendChild(probe);
                    const accent = getComputedStyle(probe).color;
                    probe.remove();
                    const button = getComputedStyle(document.querySelector('main .rz-button')).backgroundColor;
                    return [accent, button];
                }
                """);
            Assert.Equal(look[0], look[1]);
            await page.CloseAsync();
        }
    }

    [LiveServerFact]
    public async Task Signed_out_pages_have_no_navigation_one_h1_and_the_stamp_in_a_footer()
    {
        AllowError(e => e.Contains("zz-missing", StringComparison.Ordinal) && e.Contains("404", StringComparison.Ordinal));
        foreach (var (width, height) in Sizes)
        {
            foreach (var (path, heading) in new[] { ("/login", "PoRedoMedia"), ("/gallery", "PoRedoMedia"), ("/diag", "System status"), ("/zz-missing", "Page not found") })
            {
                var page = await NewTrackedPageAsync();
                await page.SetViewportSizeAsync(width, height);
                await page.GotoAsync(BaseUrl + path);
                await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading, Level = 1 })).ToBeVisibleAsync(new() { Timeout = 30_000 });

                await Assertions.Expect(page.GetByRole(AriaRole.Navigation)).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator(".bottom-nav, .shell-nav")).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator("h1")).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator("footer.po-version")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"^v\d{6}$"));
                if (path is "/login" or "/gallery")
                {
                    // The sign-in page: the name and exactly one button, Microsoft's, in every environment.
                    await Assertions.Expect(page.Locator("main").GetByRole(AriaRole.Button)).ToHaveCountAsync(1);
                    await Assertions.Expect(page.Locator("main").GetByRole(AriaRole.Button, new() { Name = "Sign in with Microsoft" })).ToBeVisibleAsync();
                }

                await page.CloseAsync();
            }
        }
    }

    [LiveServerFact]
    public async Task Signed_in_header_shows_the_exact_email_top_right()
    {
        foreach (var (width, height) in Sizes)
        {
            var email = $"e2e-{Guid.NewGuid():N}"[..20] + "@localhost";
            var page = await NewTrackedPageAsync();
            await page.SetViewportSizeAsync(width, height);
            await page.GotoAsync($"{BaseUrl}/dev-login?email={Uri.EscapeDataString(email)}&returnUrl=%2Fgallery");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Library", Level = 1 })).ToBeVisibleAsync(new() { Timeout = 30_000 });

            var shown = page.Locator(".po-header__email");
            await Assertions.Expect(shown).ToHaveTextAsync(email);
            var box = (await shown.BoundingBoxAsync())!;
            Assert.True(box.X + box.Width > width * 0.6, $"the email is not on the right at {width}px ({box.X}+{box.Width})");
            Assert.True(box.Y < 60, $"the email is not in the header at {width}px (y {box.Y})");
            await Assertions.Expect(page.Locator("h1")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("footer.po-version")).ToHaveCountAsync(1);
            await page.CloseAsync();
        }
    }

    [LiveServerFact]
    public async Task Pages_have_no_axe_violations_signed_in_and_out_on_a_laptop_and_a_phone()
    {
        AllowError(e => e.Contains("zz-missing", StringComparison.Ordinal) && e.Contains("404", StringComparison.Ordinal));
        var failures = new List<string>();

        async Task ScanAsync(IPage page, string name)
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            var result = await page.RunAxe(new AxeRunOptions { ResultTypes = [ResultType.Violations] });
            failures.AddRange(result.Violations.Select(v => $"{name}: {v.Id} ({v.Impact}) -> {string.Join(" | ", v.Nodes.Take(3).Select(n => n.Html))}"));
        }

        foreach (var (width, height) in Sizes)
        {
            foreach (var (path, heading) in new[] { ("/login", "PoRedoMedia"), ("/diag", "System status"), ("/zz-missing", "Page not found") })
            {
                var page = await NewTrackedPageAsync();
                await page.SetViewportSizeAsync(width, height);
                await page.GotoAsync(BaseUrl + path);
                await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading, Level = 1 })).ToBeVisibleAsync(new() { Timeout = 30_000 });
                await ScanAsync(page, $"out {path} @{width}");
                await page.CloseAsync();
            }

            var signedIn = await SignedInPageAsync("/");
            await signedIn.SetViewportSizeAsync(width, height);
            foreach (var (path, heading) in new[] { ("/", "Create"), ("/gallery", "Library"), ("/movie", "Create Movie"), ("/comic", "Comic-ify"), ("/diag", "System status"), ("/zz-missing", "Page not found") })
            {
                await signedIn.GotoAsync(BaseUrl + path);
                await Assertions.Expect(signedIn.GetByRole(AriaRole.Heading, new() { Name = heading, Level = 1, Exact = true })).ToBeVisibleAsync(new() { Timeout = 30_000 });
                await ScanAsync(signedIn, $"in {path} @{width}");
            }

            await signedIn.CloseAsync();
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
