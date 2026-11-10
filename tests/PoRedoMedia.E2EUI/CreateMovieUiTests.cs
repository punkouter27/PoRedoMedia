using Microsoft.Playwright;

namespace PoRedoMedia.E2EUI;

public sealed class CreateMovieUiTests : UiTestBase
{
    /// <summary>Set to a folder to keep a picture of each step as the test passes through it.</summary>
    private static readonly string? Shots = Environment.GetEnvironmentVariable("MOVIE_SHOTS");

    private static async Task ShotAsync(IPage page, string name)
    {
        if (Shots is null) return;
        Directory.CreateDirectory(Shots);
        await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, name + ".png") });
    }

    [LiveServerFact]
    public async Task A_movie_goes_from_a_film_and_a_typed_topic_to_a_rendered_video_through_every_step()
    {
        var page = await SignedInPageAsync("/movie");
        await page.SetViewportSizeAsync(1440, 900);

        // Template: a new user has no channel name set by hand, but the series starts with one, and its intro is drawn.
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Create Movie" })).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "1 · Template" }).ClickAsync();
        await Assertions.Expect(page.GetByAltText("The intro as it will look")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await ShotAsync(page, "1-template");

        // Films: say what it is about. A movie is your own videos, so with none the script cannot be written yet.
        await page.GetByRole(AriaRole.Button, new() { Name = "2 · Films" }).ClickAsync();
        await page.GetByLabel("What the movie is about").FillAsync("Our weekend building the treehouse");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Write the script" })).ToBeDisabledAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Add films" }).ClickAsync();
        await page.SetInputFilesAsync(".mv-add-films input[type=file]", new FilePayload { Name = "treehouse.mp4", MimeType = "video/mp4", Buffer = await CreateVideoUiTests.ClipAsync(8) });
        await Assertions.Expect(page.Locator(".mv-film")).ToHaveCountAsync(1, new() { Timeout = 60_000 });
        await ShotAsync(page, "2-films");
        await page.GetByRole(AriaRole.Button, new() { Name = "Write the script" }).ClickAsync();

        // Voice-over: one clip for the one film, its voice fitted to the film and recorded as it was brought in.
        await Assertions.Expect(page.GetByRole(AriaRole.Option).First).ToBeVisibleAsync(new() { Timeout = 60_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Option)).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByRole(AriaRole.Option).First).ToContainTextAsync("My video");
        await Assertions.Expect(page.GetByText("Voices · 1 of 1")).ToBeVisibleAsync();
        await ShotAsync(page, "3-voice-over");

        // A change to a clip's title can be undone.
        await page.GetByLabel("This clip's title").FillAsync("A brand new title");
        await page.GetByLabel("This clip's title").PressAsync("Enter");
        await Assertions.Expect(page.GetByRole(AriaRole.Option).First).ToContainTextAsync("A brand new title");
        await page.GetByRole(AriaRole.Button, new() { Name = "Undo" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Option).First).Not.ToContainTextAsync("A brand new title");

        // Video: render it, and the finished video is there to play, with its chapters.
        await page.GetByRole(AriaRole.Button, new() { Name = "4 · Video" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Render finished video" }).ClickAsync();
        // It asks first: a movie uses one of the day's runs.
        await Assertions.Expect(page.GetByRole(AriaRole.Alertdialog)).ToContainTextAsync("one of your runs for today");
        await page.GetByRole(AriaRole.Alertdialog).GetByRole(AriaRole.Button, new() { Name = "Render", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("The finished video")).ToBeVisibleAsync(new() { Timeout = 180_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "In your Library" })).ToBeVisibleAsync();
        await ShotAsync(page, "4-video");

        // Every saved movie can be deleted at once, after asking.
        await page.GetByRole(AriaRole.Button, new() { Name = "2 · Films" }).ClickAsync();
        await ShotAsync(page, "8-delete-all");
        await page.GetByRole(AriaRole.Button, new() { Name = "Delete all" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alertdialog)).ToContainTextAsync("deleted for good");
        await page.GetByRole(AriaRole.Alertdialog).GetByRole(AriaRole.Button, new() { Name = "Delete all" }).ClickAsync();
        await Assertions.Expect(page.GetByText("No movies yet.")).ToBeVisibleAsync(new() { Timeout = 30_000 });
    }

    [LiveServerFact]
    public async Task Films_become_a_whole_video_in_one_go_and_it_can_be_previewed_and_cut_into_teasers()
    {
        var page = await SignedInPageAsync("/movie", "po.adv.movie");
        await page.SetViewportSizeAsync(1440, 900);

        // Films: two of the user's own, chosen together and uploaded from the form itself; long enough to narrate over
        // and to cut a ten-second teaser from.
        await page.GetByRole(AriaRole.Button, new() { Name = "Add films" }).ClickAsync(new() { Timeout = 30_000 });
        await page.SetInputFilesAsync(".mv-add-films input[type=file]",
        [
            new FilePayload { Name = "treehouse.mp4", MimeType = "video/mp4", Buffer = await CreateVideoUiTests.ClipAsync(14) },
            new FilePayload { Name = "ladder.mp4", MimeType = "video/mp4", Buffer = await CreateVideoUiTests.ClipAsync(6) },
        ]);
        var film = page.Locator(".mv-film");
        await Assertions.Expect(film).ToHaveCountAsync(2, new() { Timeout = 90_000 });
        await film.First.GetByLabel("What this film shows").FillAsync("Hammering the last board in");
        await film.Last.GetByLabel("What this film shows").FillAsync("Climbing the ladder");
        await ShotAsync(page, "5-films-added");

        // The whole video in one go: it says what it will do and asks first.
        await page.GetByRole(AriaRole.Button, new() { Name = "Make the video from my films" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alertdialog)).ToContainTextAsync("Meme-ify");
        await page.GetByRole(AriaRole.Button, new() { Name = "Make it" }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("The finished video")).ToBeVisibleAsync(new() { Timeout = 240_000 });
        // It went into the Library by itself.
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "In your Library" })).ToBeVisibleAsync();

        // A clip for each film; the longer one shows its film, narrated over with the film's own sound underneath.
        await page.GetByRole(AriaRole.Button, new() { Name = "3 · Voice-over" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Option)).ToHaveCountAsync(2);
        await page.GetByRole(AriaRole.Option).Filter(new() { HasText = "14 s" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Option).First).ToContainTextAsync("My video");
        await Assertions.Expect(page.GetByRole(AriaRole.Option).First).ToContainTextAsync("voice + my sound");
        // And it went through Meme-ify before it was joined into the movie.
        await Assertions.Expect(page.GetByRole(AriaRole.Option).First).ToContainTextAsync("Memes");
        await ShotAsync(page, "5b-voice-over-memes");

        // Meme-ify can be switched off for this one clip, on its Video tab: the clip then plays as it is.
        await page.Locator(".mv-inspector").GetByText("Video", new() { Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("This clip's video")).ToBeVisibleAsync();
        await ShotAsync(page, "5c-memes-switch");
        await page.Locator(".mv-inspector .rz-switch").Last.ClickAsync();
        await Assertions.Expect(page.GetByText("No memes: the clip plays as it is")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Option).First).Not.ToContainTextAsync("Memes");

        // The quick preview, an advanced step: built once, then its captions change without building again.
        await page.GetByRole(AriaRole.Button, new() { Name = "Preview", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Build preview" }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("The preview", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = 120_000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Caption style" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Green" }).ClickAsync();
        await ShotAsync(page, "6-preview");

        // Teasers: suggested, then rendered upright.
        await page.GetByRole(AriaRole.Button, new() { Name = "4 · Video" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Suggest teasers" }).ClickAsync();
        await Assertions.Expect(page.Locator(".mv-teaser").First).ToBeVisibleAsync(new() { Timeout = 60_000 });
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("^Render \\d teasers?$") }).ClickAsync();
        await Assertions.Expect(page.GetByText("Rendered teasers")).ToBeVisibleAsync(new() { Timeout = 180_000 });
        await ShotAsync(page, "7-teasers");
    }
}
