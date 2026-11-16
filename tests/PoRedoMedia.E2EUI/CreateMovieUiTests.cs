using Microsoft.Playwright;

namespace PoRedoMedia.E2EUI;

public sealed class CreateMovieUiTests : UiTestBase
{
    /// <summary>Set to a folder to keep a picture of each stage as the test passes through it.</summary>
    private static readonly string? Shots = Environment.GetEnvironmentVariable("MOVIE_SHOTS");

    private static async Task ShotAsync(IPage page, string name)
    {
        if (Shots is null) return;
        Directory.CreateDirectory(Shots);
        await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, name + ".png") });
    }

    [LiveServerFact]
    public async Task A_titled_movie_is_made_from_one_film_on_one_page()
    {
        var page = await SignedInPageAsync("/movie");
        await page.SetViewportSizeAsync(1440, 900);

        // The channel's look is folded away; opened, its intro is drawn.
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Create Movie" })).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("Channel & intro") }).ClickAsync();
        await Assertions.Expect(page.GetByAltText("The intro as it will look")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await ShotAsync(page, "1-channel");

        // Title, episode and one film; Make stays off until there is a film.
        await page.GetByLabel("Title", new() { Exact = true }).FillAsync("Treehouse Day");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Make the video from my films" })).ToBeDisabledAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Add films" }).ClickAsync();
        await page.SetInputFilesAsync(".mv-add-films input[type=file]", new FilePayload { Name = "treehouse.mp4", MimeType = "video/mp4", Buffer = await CreateVideoUiTests.ClipAsync(8) });
        await Assertions.Expect(page.Locator(".mv-film")).ToHaveCountAsync(1, new() { Timeout = 60_000 });
        await Assertions.Expect(page.Locator(".mv-film")).ToContainTextAsync("Narrated");
        await ShotAsync(page, "2-films");

        // It says what it will do and asks first; then the finished video is there, under the typed title.
        await page.GetByRole(AriaRole.Button, new() { Name = "Make the video from my films" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alertdialog)).ToContainTextAsync("Meme-ify");
        await page.GetByRole(AriaRole.Button, new() { Name = "Make it" }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("The finished video")).ToBeVisibleAsync(new() { Timeout = 240_000 });
        await page.WaitForFunctionAsync("() => { const v = document.querySelector('video[aria-label=\"The finished video\"]'); return v !== null && v.readyState >= 2; }");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Treehouse Day" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "In your Library" })).ToBeVisibleAsync();
        await ShotAsync(page, "3-video");

        // Every saved movie can be deleted at once, after asking.
        await page.GetByRole(AriaRole.Button, new() { Name = "Delete all" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alertdialog)).ToContainTextAsync("deleted for good");
        await page.GetByRole(AriaRole.Alertdialog).GetByRole(AriaRole.Button, new() { Name = "Delete all" }).ClickAsync();
        await Assertions.Expect(page.GetByText("No movies yet.")).ToBeVisibleAsync(new() { Timeout = 30_000 });
    }

    [LiveServerFact]
    public async Task Films_with_notes_become_a_whole_video_that_can_be_cut_into_teasers()
    {
        var page = await SignedInPageAsync("/movie");
        await page.SetViewportSizeAsync(1440, 900);

        // Two films, chosen together; long enough to narrate over and to cut a ten-second teaser from.
        await page.GetByRole(AriaRole.Button, new() { Name = "Add films" }).ClickAsync(new() { Timeout = 30_000 });
        await page.SetInputFilesAsync(".mv-add-films input[type=file]",
        [
            new FilePayload { Name = "treehouse.mp4", MimeType = "video/mp4", Buffer = await CreateVideoUiTests.ClipAsync(14) },
            new FilePayload { Name = "ladder.mp4", MimeType = "video/mp4", Buffer = await CreateVideoUiTests.ClipAsync(6) },
        ]);
        var film = page.Locator(".mv-film");
        await Assertions.Expect(film).ToHaveCountAsync(2, new() { Timeout = 90_000 });

        // A film's note and narration fold away under its options.
        await film.First.GetByRole(AriaRole.Button, new() { Name = "Options for this film" }).ClickAsync();
        await film.First.GetByLabel("What this film shows").FillAsync("Hammering the last board in");
        await ShotAsync(page, "4-film-options");

        await page.GetByRole(AriaRole.Button, new() { Name = "Make the video from my films" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Make it" }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("The finished video")).ToBeVisibleAsync(new() { Timeout = 240_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Render again" })).ToBeVisibleAsync();

        // Teasers: suggested, then rendered upright.
        await page.GetByRole(AriaRole.Button, new() { Name = "Suggest teasers" }).ClickAsync();
        await Assertions.Expect(page.Locator(".mv-teaser").First).ToBeVisibleAsync(new() { Timeout = 60_000 });
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("^Render \\d teasers?$") }).ClickAsync();
        await Assertions.Expect(page.GetByText("Rendered teasers")).ToBeVisibleAsync(new() { Timeout = 180_000 });
        await ShotAsync(page, "5-teasers");
    }
}
