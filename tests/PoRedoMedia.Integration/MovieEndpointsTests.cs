using System.Net;
using System.Net.Http.Json;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.IntegrationTests;

[Collection(AzuriteCollection.Name)]
public sealed class MovieEndpointsTests(AzuriteFixture azurite)
{
    [DockerFact]
    public async Task A_movie_is_written_edited_and_undone_and_is_private_to_the_user_who_made_it()
    {
        var root = Path.Combine(Path.GetTempPath(), $"poredomedia-movies-{Guid.NewGuid():N}");
        try
        {
            using var factory = new AppFactory(azurite.ConnectionString, settings: new() { ["Movie:Root"] = root, ["RenderQuota:DailyLimit"] = "1" });
            var me = await factory.SignedInAsync($"dev|{Guid.NewGuid()}");
            var someoneElse = await factory.SignedInAsync($"dev|{Guid.NewGuid()}");

            // The General series is there before anything is set up, with its own starting kit.
            var kit = await me.GetFromJsonAsync("/api/movies/kit/General", WireJson.Default.MovieKitDto);
            Assert.Equal(("My Movies", "Movie", 1), (kit!.ChannelName, kit.Structure, kit.NextNumber));

            // A movie is the user's own videos between the intro and outro: with none, no script is written.
            var request = new MovieLessonRequest("General", "Movie", "Our weekend building the treehouse", "", 1, []);
            var refused = await me.PostAsJsonAsync("/api/movies/write", request, WireJson.Default.MovieLessonRequest);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("Add your films first", await refused.Content.ReadAsStringAsync());

            // With two films, a script is written as a job: one clip for each film and nothing between them. With no
            // script model configured its words are stand-ins.
            List<MovieFilmDto> films = [];
            foreach (var name in new[] { "deck.mp4", "ladder.mp4" })
            {
                var upload = await MediaEndpointsTests.UploadAndConfirmAsync(me, name, await MediaEndpointsTests.ClipAsync(6));
                films.Add(new((await upload.Content.ReadFromJsonAsync(WireJson.Default.MediaDto))!.Id, name, OwnSound: false));
            }
            request = request with { Films = [.. films] };
            Assert.Equal(HttpStatusCode.OK, (await me.PostAsJsonAsync("/api/movies/write", request, WireJson.Default.MovieLessonRequest)).StatusCode);
            MovieJobDto? job = null;
            for (var i = 0; i < 120 && job is not { State: not "running" }; i++)
            {
                await Task.Delay(250);
                job = await me.GetFromJsonAsync("/api/movies/job", WireJson.Default.MovieJobDto);
            }
            Assert.Equal(("done", null), (job!.State, job.Error));
            var id = job.MovieId!;
            var movie = (await me.GetFromJsonAsync($"/api/movies/{id}", WireJson.Default.MovieReply))!.Movie!;
            Assert.Equal(2, movie.Clips.Length);
            Assert.All(movie.Clips, clip => Assert.True(clip.HasFilm, $"“{clip.Title}” has no film of the user's"));
            // Nothing that is not a film can be put between them.
            Assert.Contains("Every clip is one of your own videos", (await PostAsync(me, $"/api/movies/{id}/edits", new MovieEdit("addClip", movie.Clips[0].Id))).Error);
            Assert.False(movie.CanUndo);

            // A change is saved and can be undone; one that makes no sense is refused in words, and changes nothing.
            var first = movie.Clips[0];
            var renamed = await PostAsync(me, $"/api/movies/{id}/edits", new MovieEdit("clipTitle", first.Id, "A new title"));
            Assert.Equal(("A new title", true), (renamed.Movie!.Clips[0].Title, renamed.Movie.CanUndo));
            Assert.NotNull((await PostAsync(me, $"/api/movies/{id}/edits", new MovieEdit("kind", first.Id, "Hologram"))).Error);
            var undone = await (await me.PostAsync($"/api/movies/{id}/undo", null)).Content.ReadFromJsonAsync(WireJson.Default.MovieReply);
            Assert.Equal((first.Title, true), (undone!.Movie!.Clips[0].Title, undone.Movie.CanRedo));

            // Meme-ify can be switched off for one clip, and back on, as an edit like any other.
            var quiet = await PostAsync(me, $"/api/movies/{id}/edits", new MovieEdit("memes", first.Id, Flag: false));
            Assert.Equal((false, true), (quiet.Movie!.Clips[0].MemesOn, quiet.Movie.Clips[1].MemesOn));
            Assert.True((await PostAsync(me, $"/api/movies/{id}/edits", new MovieEdit("memes", first.Id, Flag: true))).Movie!.Clips[0].MemesOn);

            // Rendering the movie is one run of the day's allowance (one here); with none left, the next is refused in words.
            Assert.Equal(HttpStatusCode.OK, (await me.PostAsync($"/api/movies/{id}/render", null)).StatusCode);
            for (var i = 0; i < 600; i++)
            {
                await Task.Delay(250);
                job = await me.GetFromJsonAsync("/api/movies/job", WireJson.Default.MovieJobDto);
                if (job is { State: not "running" } && job.Title.StartsWith("Rendering", StringComparison.Ordinal))
                    break;
            }
            Assert.Equal("done", job!.State);
            var again = await me.PostAsync($"/api/movies/{id}/render", null);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Contains("all 1 for today are used", (await again.Content.ReadFromJsonAsync(WireJson.Default.MovieReply))!.Error);

            // Nobody else can see it, list it, change it or delete it; to them there is no such movie.
            Assert.Empty((await someoneElse.GetFromJsonAsync("/api/movies", WireJson.Default.ListMovieSummaryDto))!);
            Assert.Equal(HttpStatusCode.NotFound, (await someoneElse.GetAsync($"/api/movies/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await someoneElse.GetAsync($"/api/movies/{id}/files/episode.json")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await someoneElse.PostAsync($"/api/movies/{id}/undo", null)).StatusCode);
            Assert.Single((await me.GetFromJsonAsync("/api/movies", WireJson.Default.ListMovieSummaryDto))!);

            // Only media is served from a movie's folder, and only from inside it.
            Assert.Equal(HttpStatusCode.NotFound, (await me.GetAsync($"/api/movies/{id}/files/episode.json")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await me.GetAsync($"/api/movies/{id}/files/..%2F..%2Fpace.json")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await me.GetAsync("/api/movies/..%2F..%2Fwindows/files/win.ini")).StatusCode);

            // A write without the antiforgery token is refused like every other JSON write.
            var tokenless = factory.CreateClient();
            tokenless.DefaultRequestHeaders.Add("X-Fake-User", "dev|tokenless");
            Assert.Equal(HttpStatusCode.BadRequest, (await tokenless.PostAsJsonAsync("/api/movies/write", request, WireJson.Default.MovieLessonRequest)).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await me.DeleteAsync($"/api/movies/{id}")).StatusCode);
            Assert.Empty((await me.GetFromJsonAsync("/api/movies", WireJson.Default.ListMovieSummaryDto))!);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<MovieReply> PostAsync(HttpClient client, string path, MovieEdit edit) =>
        (await (await client.PostAsJsonAsync(path, edit, WireJson.Default.MovieEdit)).Content.ReadFromJsonAsync(WireJson.Default.MovieReply))!;
}
