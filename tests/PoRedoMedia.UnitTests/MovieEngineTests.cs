using PoRedoMedia.Api.Features.Movies;
using PoRedoMedia.Api.Features.Render;
using PoRedoMedia.Api.Features.Video;
using PoRedoMedia.Movie.Library;
using PoRedoMedia.Movie.Pictures;
using PoRedoMedia.Movie.Render;
using PoRedoMedia.Movie.Script;
using PoRedoMedia.Movie.Voice;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.UnitTests;

/// <summary>
/// The movie engine as it was changed on its way here from PoAutoRobo: the General series, picture models that are
/// not GPT-image, and the ids and files the web is allowed to ask for. The engine's own behaviour is tested where it
/// came from.
/// </summary>
public sealed class MovieEngineTests
{
    [Fact]
    public void Each_series_has_its_own_shapes_and_a_shape_says_which_series_it_is()
    {
        foreach (var show in Enum.GetValues<Show>())
        {
            Assert.NotEmpty(BeatPlan.Structures(show));
            Assert.All(BeatPlan.Structures(show), structure => Assert.Equal(show, BeatPlan.ShowOf(structure)));
            Assert.Equal(show, BeatPlan.ShowOf(SeriesKit.StarterFor(show).Structure));
        }

        // A movie about anything opens and closes with pictures, and its films go in the middle.
        Assert.Equal([("Opening", false), ("Highlights", true), ("Closing", false)], BeatPlan.Beats(BeatStructure.Movie).Select(b => (b.Name, b.ForFootage)));
        // Saved movies name their shape and series, so the ones that were there first keep their places.
        Assert.Equal((0, 4, 0, 1), ((int)BeatStructure.Story, (int)BeatStructure.Highlights, (int)Show.Robot, (int)Show.Kids));
    }

    [Fact]
    public void The_general_series_is_told_nothing_about_the_robot_or_the_boy()
    {
        var kinds = Visuals.Kinds;
        string[] prompts =
        [
            ScriptSchemas.OutlineSystem(kinds, Show.General), ScriptSchemas.ClipSystem(Show.General), ScriptSchemas.TierSystem(Show.General),
            ScriptSchemas.ReviseSystem(Show.General), ScriptSchemas.WatchSystem(Show.General), ScriptSchemas.RewriteSystem(Show.General),
            ScriptSchemas.TeasersSystem(Show.General), ScriptSchemas.ShotsSystem(Show.General), ScriptSchemas.PublishSystem(Show.General),
        ];
        Assert.All(prompts, prompt =>
        {
            Assert.DoesNotContain("R1", prompt);
            Assert.DoesNotContain("robot", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("little boy", prompt);
        });
        // Nobody presents a general movie on screen, and no picture is made of the people in it.
        Assert.Contains("Set \"host\" to false for every clip", prompts[0]);
        Assert.Contains("Never describe a picture of the people in the topic", prompts[0]);
        // The presets are as they were.
        Assert.Contains("Unitree R1", ScriptSchemas.OutlineSystem(kinds, Show.Robot));
        Assert.Contains("Never describe a picture of the boy", ScriptSchemas.OutlineSystem(kinds, Show.Kids));
    }

    [Fact]
    public async Task A_stand_in_script_for_a_general_movie_follows_its_beats_and_names_no_robot()
    {
        var episode = await new MockScriptWriter().WriteEpisodeAsync(new Lesson("Our weekend building the treehouse", "", BeatStructure.Movie, []), [], EpisodeLength.Standard, default);

        Assert.Equal(["Opening", "Highlights", "Closing"], episode.Clips.Select(c => c.Beat).Distinct());
        Assert.All(episode.Clips, clip =>
        {
            Assert.False(clip.HostVisible);
            Assert.DoesNotContain("R1", clip.Title + clip.Active.Dialogue);
            Assert.NotEqual("", clip.Active.Dialogue.Trim());
        });
    }

    [Fact]
    public void Only_gpt_image_draws_drafts_and_see_through_pictures_and_other_models_cost_what_the_host_says()
    {
        Assert.True(ImageModels.HasQuality("gpt-image-1-mini") && ImageModels.HasTransparency("gpt-image-1-mini"));
        foreach (var model in new[] { "gemini-2.5-flash-image", "FLUX.1-Kontext-pro", "mock" })
            Assert.False(ImageModels.HasQuality(model) || ImageModels.HasTransparency(model));

        var before = CostEstimate.HostPicturePrice;
        try
        {
            CostEstimate.HostPicturePrice = 0.039m;
            Assert.Equal(0.039m, CostEstimate.PriceOf("gemini-2.5-flash-image"));
            Assert.Equal(CostEstimate.MiniPicturePrice, CostEstimate.PriceOf("gpt-image-1-mini")); // the list price it has always had
        }
        finally
        {
            CostEstimate.HostPicturePrice = before;
        }
    }

    [Fact]
    public void A_movie_id_is_a_folder_name_the_engine_made_and_nothing_else()
    {
        foreach (var id in new[] { "01-our-weekend", "episode", "a-2", ProjectStore.Slug("Ünïcode & Spaces!") })
            Assert.True(MovieStudio.IsId(id), id);
        foreach (var id in new[] { "", "..", "../x", "a/b", @"a\b", "Upper", "a b", "a.json", new string('a', 81), null })
            Assert.False(MovieStudio.IsId(id), id);

        // What may be sent as a kit's or a movie's picture or sound goes by its type alone.
        Assert.Null(MovieStudio.Accepts("logo", "mine.PNG"));
        Assert.Null(MovieStudio.Accepts("music", "tune.mp3"));
        Assert.NotNull(MovieStudio.Accepts("logo", "tune.mp3"));
        Assert.NotNull(MovieStudio.Accepts("music", "run.exe"));
        Assert.NotNull(MovieStudio.Accepts("sting", "playlist.m3u8"));
    }

    [Fact]
    public void Every_clip_is_meme_ified_as_it_plays_with_a_roast_except_in_a_kids_video()
    {
        HashSet<MediaFunction> everything = [MediaFunction.Memeify, MediaFunction.VideoRoast, MediaFunction.Captions];
        Assert.Equal([MediaFunction.Memeify, MediaFunction.VideoRoast], MovieStudio.MemeRun(Show.General, everything, narrated: false).Functions);
        Assert.Equal([MediaFunction.Memeify, MediaFunction.VideoRoast], MovieStudio.MemeRun(Show.Robot, everything, narrated: true).Functions);
        // No spoken insults over a little boy, and the mildest director.
        var kids = MovieStudio.MemeRun(Show.Kids, everything, narrated: true);
        Assert.Equal([MediaFunction.Memeify], kids.Functions);
        Assert.Equal("sitcom", kids.Options[RunOptions.VideoPersona]);
        Assert.Equal("1", kids.Options[RunOptions.VideoVoiceFirst]); // the narrator stays on top of the meme sounds, kids' clips too
        // A server with no roast voice still meme-ifies.
        Assert.Equal([MediaFunction.Memeify], MovieStudio.MemeRun(Show.General, new HashSet<MediaFunction> { MediaFunction.Memeify }, narrated: true).Functions);

        // What Meme-ify is handed is the clip as it plays: an AI video repeated, or a film held on its last frame, for
        // exactly as long as its voice-over, with that voice-over as its sound.
        var narration = new Narration("voice.wav", TimeSpan.FromSeconds(12.5), []);
        var ai = MovieStudio.VoicedClip("ai.mp4", loop: true, narration, "out.mp4");
        Assert.Equal(["-y", "-stream_loop", "-1", "-i", "ai.mp4", "-i", "voice.wav"], ai.Take(7));
        Assert.Equal("12.5", ai[ai.ToList().IndexOf("-t") + 1]);
        Assert.Contains(ai, a => a.Contains("tpad=stop_mode=clone:stop_duration=12.5"));
        Assert.Equal(["-map", "[v]", "-map", "1:a"], ai.SkipWhile(a => a != "-map").Take(4));
        Assert.Equal(["-y", "-i", "film.mp4"], MovieStudio.VoicedClip("film.mp4", loop: false, narration, "out.mp4").Take(3));

        // The movie hands over what is said in it, in lines (a sentence, or twelve words at most), so Meme-ify times its jokes after them without paying to hear it.
        var words = Durations.SplitWords("Here we go. The ladder slips! Then we climb up to the very top of the brand new treehouse together")
            .Select((w, i) => new WordTiming(w, TimeSpan.FromSeconds(i * 0.4), TimeSpan.FromSeconds(0.3))).ToList();
        var narrated = new Clip(Guid.NewGuid(), "Up", Tier.B, new Dictionary<Tier, TierScript> { [Tier.B] = new("x", "x", "none") }, new VisualSpec(VisualKind.UserVideo), false);
        var lines = MovieStudio.Known(narrated, "film.mp4", narration with { Words = words })!;
        Assert.Equal(["Here we go.", "The ladder slips!", "Then we climb up to the very top of the brand new", "treehouse together"], lines.Select(l => l.Text));
        Assert.Equal((0.0, 1.1), (lines[0].StartSeconds, Math.Round(lines[0].EndSeconds, 1)));
        // A film that keeps its own sound and was never listened to has only guessed times: those are not handed over.
        Assert.Null(MovieStudio.Known(narrated with { OwnSound = true }, Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4"), narration with { Words = words }));

        // Each clip under a minute, the whole movie up to ten, films a minute with the Library's slack.
        Assert.Equal((62, 600, 62), ((int)Durations.MaxClip.TotalSeconds, (int)Durations.MaxMovie.TotalSeconds, (int)Conformance.MaxFootage.TotalSeconds));
    }

    [Fact]
    public void Over_a_narrator_the_roast_heckles_in_the_pauses_and_never_cuts_into_a_line()
    {
        // A narrated clip asks the roast to answer the narrator; one that keeps its own sound does not.
        HashSet<MediaFunction> everything = [MediaFunction.Memeify, MediaFunction.VideoRoast];
        Assert.Equal("1", MovieStudio.MemeRun(Show.General, everything, narrated: true).Options[RunOptions.VideoRoastNarrated]);
        Assert.False(MovieStudio.MemeRun(Show.General, everything, narrated: false).Options.ContainsKey(RunOptions.VideoRoastNarrated));

        static RenderVisualEntry Joke(long at, int ms) => new(at, "joke.mp3", null, null, null, Voice: true, DurationMs: ms);
        static RenderVisualEntry Sound(long at, int ms) => new(at, "boom.mp3", null, null, null, DurationMs: ms);
        // The narrator speaks 0–4 s and 6–9 s. A joke due at 2 s waits for 4 s; one due in the 4–6 s pause keeps its time.
        (long, long)[] narrator = [(0, 4000), (6000, 9000)];
        var arranged = TurnTaking.Arrange([Joke(2000, 1500), Joke(4800, 1000), Sound(500, 800)], 12_000, narrator);
        Assert.Equal([4200L, 5900L], arranged.Cues.Where(c => c.Voice).Select(c => c.TimestampMs)); // the second waits its turn behind the first
        Assert.Equal(500, arranged.Cues.Single(c => !c.Voice).TimestampMs);
        // With no meme sounds to share with, a joke still waits for the line; one pushed past the end is left out.
        var alone = TurnTaking.Arrange([Joke(7000, 2000), Joke(1000, 1000)], 10_000, [(6000, 9500), (0, 500)]);
        Assert.Equal([1000L], alone.Cues.Select(c => c.TimestampMs));
        Assert.Equal((1, 1), (alone.JokesMoved, alone.JokesDropped));
        // Without a narrator, nothing changes from before.
        Assert.Equal([2000L, 4800L], TurnTaking.Arrange([Joke(2000, 1500), Joke(4800, 1000)], 12_000).Cues.Select(c => c.TimestampMs));
    }

    [Fact]
    public void Captions_are_drawn_with_a_font_that_is_there_and_the_sign_in_page_is_googles_own()
    {
        // FFmpeg is handed the font by file, from the working folder: a server has none installed to look up by name.
        var args = FfmpegArgs.ClipVideo(ClipSource.Image, "picture.png", TimeSpan.FromSeconds(5), ExportPreset.Hd30, "out.mp4", "captions.ass");
        Assert.Contains(args, arg => arg.Contains("ass=captions.ass:fontsdir=."));
        Assert.Equal(OperatingSystem.IsWindows() ? "Segoe UI" : Fonts.FallbackFamily, Fonts.Family("Segoe UI"));

        var youTube = new YouTube("client id", "secret", (_, _) => throw new InvalidOperationException("nothing is sent to build a link"));
        var (verifier, challenge) = YouTube.NewProof();
        var page = youTube.SignInPage("https://example.test/api/movies/youtube/callback", "state-1", challenge);
        Assert.Equal("accounts.google.com", page.Host);
        Assert.Contains("client_id=client%20id", page.Query);
        Assert.Contains("redirect_uri=https%3A%2F%2Fexample.test%2Fapi%2Fmovies%2Fyoutube%2Fcallback", page.Query);
        Assert.Contains($"code_challenge={challenge}", page.Query);
        Assert.DoesNotContain(verifier, page.Query); // the secret half of the proof stays here
        Assert.DoesNotContain("secret", page.Query);
    }
}
