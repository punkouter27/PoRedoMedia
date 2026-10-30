using Microsoft.Playwright;

namespace PoRedoMedia.E2EUI;

public sealed class NavigationUiTests : UiTestBase
{
    /// <summary>"Good" cumulative layout shift, by the Web Vitals scale.</summary>
    private const double MaxLayoutShift = 0.1;

    // Adds up every layout shift not caused by the user since the document started.
    private const string MeasureLayoutShift = """
        window.__poShift = 0;
        new PerformanceObserver((list) => {
            for (const entry of list.getEntries()) if (!entry.hadRecentInput) window.__poShift += entry.value;
        }).observe({ type: 'layout-shift', buffered: true });
        """;

    private static readonly (string Path, string Heading)[] Routes =
    [
        ("/", "Create"),
        ("/library", "Library"),
        ("/settings", "Settings"),
    ];

    [LiveServerFact]
    public async Task Every_nav_route_and_link_works_without_console_errors_or_layout_shift()
    {
        var page = await SignedInPageAsync("/");
        var errors = new List<string>();
        page.Console += (_, m) => { if (m.Type == "error") errors.Add($"{page.Url}: {m.Text}"); };
        page.PageError += (_, e) => errors.Add($"{page.Url}: {e}");
        await page.AddInitScriptAsync(MeasureLayoutShift);
        // Leaving a page while the runtime still downloads aborts those fetches, which logs errors that are the test's own.
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Each route, loaded fresh: it renders its heading and does not jump about while loading.
        foreach (var (path, heading) in Routes)
        {
            await page.GotoAsync(BaseUrl + path);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading, Level = 1 })).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1000);
            var shift = await page.EvaluateAsync<double>("() => window.__poShift");
            Assert.True(shift < MaxLayoutShift, $"{path} shifted by {shift:0.000}");
        }

        // Every link in the header, clicked: each lands on its page.
        var nav = page.GetByRole(AriaRole.Navigation, new() { Name = "Main", Exact = true });
        foreach (var (link, heading) in new[] { ("Library", "Library"), ("Create", "Create") })
        {
            await nav.GetByRole(AriaRole.Link, new() { Name = link }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading, Level = 1 })).ToBeVisibleAsync();
        }

        await page.GetByRole(AriaRole.Link, new() { NameRegex = new("account and settings$") }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Settings", Level = 1 })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "PoRedoMedia" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Create", Level = 1 })).ToBeVisibleAsync();

        // The Runs button opens its drawer, with something to do even when nothing has run.
        await page.GetByRole(AriaRole.Button, new() { Name = "Recent runs" }).First.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Go to Create" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Go to Create" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Go to Create" })).ToHaveCountAsync(0);

        // The keys: ? lists them, / finds the Library's search.
        await page.Keyboard.PressAsync("?");
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog, new() { Name = "Keyboard shortcuts" })).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog, new() { Name = "Keyboard shortcuts" })).ToHaveCountAsync(0);
        await nav.GetByRole(AriaRole.Link, new() { Name = "Library" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Nothing here yet")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await page.Locator("body").ClickAsync(new() { Position = new() { X = 5, Y = 300 } });
        await page.Keyboard.PressAsync("/");
        await Assertions.Expect(page.GetByRole(AriaRole.Textbox, new() { Name = "Search titles and words" })).ToBeFocusedAsync();

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}
