using Microsoft.Playwright;

namespace PoRedoMedia.E2EUI;

public sealed class DiagUiTests : UiTestBase
{
    [LiveServerFact]
    public async Task Diag_opens_signed_out_on_a_phone_and_a_laptop_and_an_unknown_page_is_the_apps_own()
    {
        // System status needs no sign-in, and fit both widths with no sideways scroll. Any console error fails the test.
        var page = await NewTrackedPageAsync();
        foreach (var (width, height) in new[] { (1366, 900), (390, 844) })
        {
            await page.SetViewportSizeAsync(width, height);
            await page.GotoAsync(BaseUrl + "/diag");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "System status", Level = 1 })).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "API", Exact = true })).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Data", Exact = true })).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Services" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("Storage account")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Refresh" })).ToBeEnabledAsync();
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"), $"/diag scrolls sideways at {width}px");
        }

        // A link to a page that does not exist shows the app's "Page not found", in the shell, with a real 404.
        AllowError(e => e.Contains("zz-missing", StringComparison.Ordinal) && e.Contains("404", StringComparison.Ordinal));
        var signedIn = await SignedInPageAsync("/zz-missing");
        await Assertions.Expect(signedIn.GetByRole(AriaRole.Heading, new() { Name = "Page not found" })).ToBeVisibleAsync(new() { Timeout = 30_000 });
        // The signed-in shell: the header shows the account's email (SignedInPageAsync signs in as e2e-<guid>@localhost).
        await Assertions.Expect(signedIn.Locator(".po-header__email")).ToContainTextAsync("@localhost");
    }
}
