using PoRedoMedia.Movie.Library;
using PoRedoMedia.Movie.Render;

namespace PoRedoMedia.Api.Features.Movies;

// What makes a movie made of the owner's films more than its films end to end: its best moment teased before the intro
// (a cold open) and shown again slowed down after it happens (a replay), a whoosh and a streak of light on every cut,
// and a music bed under the clips. Only for the rendered copy: the saved movie keeps its clips as they are.
public sealed partial class MovieStudio
{
    /// <summary>How much of the best moment the cold open shows, and how much the replay slows down.</summary>
    private static readonly TimeSpan ColdOpenLength = TimeSpan.FromSeconds(4), ReplayLength = TimeSpan.FromSeconds(2.5);

    internal const string ColdOpenLabel = "COMING UP", ReplayLabel = "REPLAY";

    /// <param name="episode">The movie as it is rendered: each clip its Meme-ify version.</param>
    private async Task<Episode> ShowtimeAsync(Job job, OpenMovie open, Episode episode, CancellationToken ct)
    {
        // A movie made of films alone: one with clips of its own (pictures, title cards) is a script, not a reel.
        if (episode.Clips.Count < 2 || !open.Episode.Clips.All(c => FootageOf(c) is not null))
            return episode;
        var shown = episode with { EveryCut = true, BedPath = await BedAsync(job, open, episode, ct) };
        if (Payoff(shown.Clips) is not { } index)
            return shown;

        var clip = shown.Clips[index];
        var film = FootageOf(clip)!;
        try
        {
            Report(job, "Cutting the cold open and the replay of the best moment", 0.02);
            var length = await engine.Runner.ProbeDurationAsync(film, ct);
            // The moment in the original film is at the same time in its Meme-ify version, which plays it from the start.
            var highlight = clip.Highlight ?? length.TotalSeconds / 2;
            var key = Files.TextHash(FormattableString.Invariant($"{film}|{new FileInfo(film).Length}|{File.GetLastWriteTimeUtc(film).Ticks}|{highlight:0.###}"))[..16];
            var folder = Path.Combine(open.Folder, "showtime");
            var cold = await engine.Builder.MomentAsync(film, FfmpegArgs.MomentStart(highlight, ColdOpenLength, length), ColdOpenLength, ColdOpenLabel, 1,
                Path.Combine(folder, $"{key}-open.mp4"), ct);
            var replay = await engine.Builder.MomentAsync(film, FfmpegArgs.MomentStart(highlight, ReplayLength, length), ReplayLength, ReplayLabel, FfmpegArgs.ReplaySlowdown,
                Path.Combine(folder, $"{key}-replay.mp4"), ct);
            // The replay follows the clip it replays, in its beat, so the chapters run on through it.
            // No beat for the cold open: it carries its own label, not a beat's strap, and is a chapter by its title.
            List<Clip> clips = [Cut("Coming up", cold, null), .. shown.Clips.Take(index + 1), Cut("Replay", replay, clip.Beat), .. shown.Clips.Skip(index + 1)];
            return shown with { Clips = clips, Opening = 1 };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The movie is still whole without them.
            job.Detail = $"The cold open and replay were left out: {Plain(e)}";
            return shown;
        }

        static Clip Cut(string title, string path, string? beat) => new(
            Guid.NewGuid(), title, Tier.B, new Dictionary<Tier, TierScript> { [Tier.B] = new("", "", "none") },
            new VisualSpec(VisualKind.UserVideo, KindLocked: true, UserVideoPath: path), HostVisible: false)
        {
            Beat = beat,
            OwnSound = true,
            NoMemes = true,
        };
    }

    /// <summary>
    /// The clip the movie's teaser and replay come from: the one the plan named as the payoff, else the last one a
    /// model watched (the story builds to its end), else the last one. Null for a movie without films.
    /// </summary>
    internal static int? Payoff(IReadOnlyList<Clip> clips)
    {
        var films = clips.Select((c, i) => (c, i)).Where(x => FootageOf(x.c) is not null).ToList();
        if (films.Count == 0) return null;
        return films.Where(x => x.c.Payoff).Select(x => (int?)x.i).FirstOrDefault()
            ?? films.Where(x => x.c.Highlight is not null).Select(x => (int?)x.i).LastOrDefault()
            ?? films[^1].i;
    }

    /// <summary>
    /// The music under the clips: the series' own music when it has some, else a light instrumental made once for the
    /// movie and kept beside it. Null when there is none and none can be made.
    /// </summary>
    private async Task<string?> BedAsync(Job job, OpenMovie open, Episode episode, CancellationToken ct)
    {
        if (episode.MusicPath is { } music && File.Exists(music)) return music;
        if (engine.Music is not { } composer) return null;
        var show = BeatPlan.ShowOf(episode.Structure);
        var folder = Path.Combine(open.Folder, "showtime");
        if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, $"bed-{show}.*").FirstOrDefault() is { } made)
            return made;
        try
        {
            Report(job, "Making the music bed", 0.01);
            var track = await composer.GenerateAsync("[Instrumental]", BedStyle(show), ct);
            if (track.Refused || track.Audio.Length == 0) return null;
            var path = Path.Combine(folder, $"bed-{show}{(track.ContentType.Contains("wav", StringComparison.OrdinalIgnoreCase) ? ".wav" : ".mp3")}");
            Files.EnsureFolderFor(path);
            await File.WriteAllBytesAsync(path, track.Audio, ct);
            SpendLog.Add(open.Folder, "music bed", engine.MusicPrice, 1);
            return path;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null; // a movie without a bed is still a movie
        }
    }

    /// <summary>What the music bed sounds like: light and instrumental, made to sit under a voice.</summary>
    internal static string BedStyle(Show show) => show switch
    {
        Show.Kids => "Instrumental only, no vocals. A light, bouncy, playful background track for a children's video: ukulele, glockenspiel, soft claps and pizzicato strings, about 110 bpm, cheerful and gentle, steady from start to end, made to sit quietly under a narrator.",
        Show.Robot => "Instrumental only, no vocals. A light, curious electronic background track for a science and robotics video: soft synth plucks, gentle beat, about 100 bpm, steady from start to end, made to sit quietly under a narrator.",
        _ => "Instrumental only, no vocals. A light, upbeat background track for a fun home video: acoustic guitar, light percussion and whistled hooks, about 105 bpm, steady from start to end, made to sit quietly under a narrator.",
    };
}
