using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace PoRedoMedia.E2EUI;

public sealed class CaptureAndAccessibilityUiTests : UiTestBase
{
    private static readonly string Shots = Path.Combine(Path.GetTempPath(), "poredomedia-shots");

    [LiveServerFact(NeedsFFmpeg = true)]
    public async Task A_video_recorded_with_the_camera_becomes_the_picked_media_and_can_be_roasted()
    {
        var page = await SignedInPageAsync("/");
        await page.GetByRole(AriaRole.Button, new() { Name = "Use the camera" }).ClickAsync(new() { Timeout = 30_000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Record a video" }).ClickAsync();
        await Assertions.Expect(page.GetByText(new System.Text.RegularExpressions.Regex(@"Recording 0:0[2-9] of 1:00"))).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Stop and use the video" }).ClickAsync();

        await Assertions.Expect(page.GetByText(new System.Text.RegularExpressions.Regex(@"camera-\d+\.webm · video"))).ToBeVisibleAsync(new() { Timeout = 60_000 });
        await page.Locator("[data-fn=VideoRoast] [role=checkbox]").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Done. The results are in your gallery.")).ToBeVisibleAsync(new() { Timeout = 120_000 });
        await Assertions.Expect(page.Locator("figure.run-result video")).ToHaveCountAsync(1);
    }

    [LiveServerFact]
    public async Task A_photo_taken_with_the_camera_becomes_the_picked_media()
    {
        var page = await SignedInPageAsync("/");
        await page.GetByRole(AriaRole.Button, new() { Name = "Use the camera" }).ClickAsync(new() { Timeout = 30_000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Take the photo" }).ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What should happen to this image?" })).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(page.GetByText(new System.Text.RegularExpressions.Regex(@"camera-\d+\.jpg · image"))).ToBeVisibleAsync();
    }

    [LiveServerFact]
    public async Task A_rap_roast_plays_on_the_run_page_with_its_lyrics()
    {
        var page = await SignedInPageAsync("/");
        await page.GetByRole(AriaRole.Button, new() { Name = "Use the camera" }).ClickAsync(new() { Timeout = 30_000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Take the photo" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What should happen to this image?" })).ToBeVisibleAsync(new() { Timeout = 30_000 });

        await page.Locator("[data-fn=RapRoast] [role=checkbox]").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Done. The results are in your gallery.")).ToBeVisibleAsync(new() { Timeout = 120_000 });

        await Assertions.Expect(page.Locator("figure.run-result audio[controls]")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByRole(AriaRole.List, new() { Name = "Lyrics. Pick a line to play from there." })).ToBeVisibleAsync();
    }

    [LiveServerFact]
    public async Task The_app_is_installable_and_its_main_pages_have_no_WCAG_AA_violations()
    {
        Directory.CreateDirectory(Shots);
        var failures = new List<string>();

        async Task ScanAsync(IPage page, string name)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, name + ".png"), FullPage = true });
            var result = await page.RunAxe(new AxeRunOptions
            {
                RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"] },
            });
            failures.AddRange(result.Violations.Select(v => $"{name}: {v.Id} ({v.Impact}) {v.Help} -> {string.Join(" | ", v.Nodes.Take(3).Select(n => n.Html))}"));
        }

        var anonymous = await NewTrackedPageAsync();
        await anonymous.GotoAsync(BaseUrl + "/login");
        await Assertions.Expect(anonymous.GetByRole(AriaRole.Heading, new() { Name = "PoRedoMedia", Level = 1 })).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await ScanAsync(anonymous, "login");

        // Installable: the page links a manifest that names the app and offers both icon sizes.
        var manifest = await anonymous.APIRequest.GetAsync(BaseUrl + "/manifest.webmanifest");
        Assert.True(manifest.Ok);
        var json = (await manifest.JsonAsync())!.Value;
        Assert.Equal("standalone", json.GetProperty("display").GetString());
        Assert.Contains(json.GetProperty("icons").EnumerateArray(), i => i.GetProperty("sizes").GetString() == "512x512");
        Assert.Equal(1, await anonymous.Locator("link[rel=manifest]").CountAsync());

        var page = await SignedInPageAsync("/", "poredomedia:adv:device-ai", "poredomedia:adv:costs", "poredomedia:adv:recipes");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Choose a photo or a video" })).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await ScanAsync(page, "create-1-media");

        await page.GetByRole(AriaRole.Button, new() { Name = "Use the camera" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Take the photo" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What should happen to this image?" })).ToBeVisibleAsync(new() { Timeout = 30_000 });
        foreach (var function in (string[])["Restyle", "MemeCaption", "RapRoast", "PhotoToVideo"])
            await page.Locator($"[data-fn={function}] [role=checkbox]").ClickAsync();
        await ScanAsync(page, "create-2-functions");

        await page.GotoAsync(BaseUrl + "/gallery");
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("camera-") }).First.ClickAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Share", Exact = true })).ToBeVisibleAsync();
        await ScanAsync(page, "gallery");

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
