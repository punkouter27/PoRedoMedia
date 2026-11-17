using PoRedoMedia.Api.Features.Movies;

namespace PoRedoMedia.UnitTests;

public class MovieSweeperTests
{
    [Fact]
    public void Movies_and_staged_films_left_idle_are_removed_unless_their_user_is_working()
    {
        var root = Directory.CreateTempSubdirectory("movies-").FullName;
        try
        {
            var now = DateTime.UtcNow;
            string Make(string relative, TimeSpan age)
            {
                var file = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "x");
                File.SetLastWriteTimeUtc(file, now - age);
                Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(file)!, now - age);
                return file;
            }

            var old = Make("a/episodes/old/film.mp4", MovieSweeper.MaxIdle + TimeSpan.FromMinutes(1));
            var fresh = Make("a/episodes/fresh/film.mp4", TimeSpan.FromMinutes(5));
            var staged = Make("a/staging/film.mp4", MovieSweeper.MaxIdle * 2);
            var kit = Make("a/series-kids/kit.json", MovieSweeper.MaxIdle * 2);
            var busy = Make("b/episodes/old/film.mp4", MovieSweeper.MaxIdle * 2);

            var removed = MovieSweeper.Sweep(root, now, home => Path.GetFileName(home) == "b");

            Assert.Equal(2, removed);
            Assert.False(Directory.Exists(Path.GetDirectoryName(old)));
            Assert.False(File.Exists(staged));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(kit));
            Assert.True(File.Exists(busy));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
