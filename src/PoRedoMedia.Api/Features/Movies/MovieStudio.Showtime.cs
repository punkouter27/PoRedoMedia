using PoRedoMedia.Movie.Library;
using PoRedoMedia.Movie.Render;

namespace PoRedoMedia.Api.Features.Movies;

// What makes a movie made of the owner's films more than its films end to end: its best moment shown again slowed down
// after it happens (a replay), a whoosh and a streak of light on every cut, and a music bed under the clips. The intro
// and outro are the series' own, the same in every movie. Only for the rendered copy: the saved movie keeps its clips.
public sealed partial class MovieStudio
{
    /// <summary>How much of the best moment the replay shows, before it is slowed down.</summary>
    private static readonly TimeSpan ReplayLength = TimeSpan.FromSeconds(2.5);

    internal const string ReplayLabel = "REPLAY";

    /// <param name="episode">The movie as it is rendered: each clip its Meme-ify version.</param>
    private async Task<Episode> ShowtimeAsync(Job job, OpenMovie open, Episode episode, CancellationToken ct)
    {
        // A movie made of films alone: one with clips of its own (pictures, title cards) is a script, not a reel.
        if (episode.Clips.Count < 2 || !open.Episode.Clips.All(c => FootageOf(c) is not null))
            return episode;
        // A movie whose films are sorted into scenes gets a whip only where the scene changes; otherwise on every cut.
        var shown = episode with { EveryCut = !episode.Clips.Any(c => c.Scene is not null), BedPath = await BedAsync(job, open, episode, ct) };
        if (Payoff(shown.Clips) is not { } index)
            return shown;

        var clip = shown.Clips[index];
        var film = FootageOf(clip)!;
        try
        {
            Report(job, "Cutting the replay of the best moment", 0.02);
            var length = await engine.Runner.ProbeDurationAsync(film, ct);
            // The moment in the original film is at the same time in its Meme-ify version, which plays it from the start.
            var highlight = clip.Highlight ?? length.TotalSeconds / 2;
            var key = Files.TextHash(FormattableString.Invariant($"{film}|{new FileInfo(film).Length}|{File.GetLastWriteTimeUtc(film).Ticks}|{highlight:0.###}"))[..16];
            var folder = Path.Combine(open.Folder, "showtime");
            var replay = await engine.Builder.MomentAsync(film, FfmpegArgs.MomentStart(highlight, ReplayLength, length), ReplayLength, ReplayLabel, FfmpegArgs.ReplaySlowdown,
                Path.Combine(folder, $"{key}-replay.mp4"), ct);
            // The replay follows the clip it replays, in its beat, so the chapters run on through it.
            // A scene of its own, so it is whipped into and out of.
            List<Clip> clips = [.. shown.Clips.Take(index + 1), Cut("Replay", replay, clip.Beat) with { Scene = clip.Scene is null ? null : -1 }, .. shown.Clips.Skip(index + 1)];
            return shown with { Clips = clips };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The movie is still whole without it.
            job.Detail = $"The replay was left out: {Plain(e)}";
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
    /// The clip the movie's replay comes from: the one the plan named as the payoff, else the last one a
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
        var name = episode.Mood is { Length: > 0 } mood ? $"bed-{show}-{mood}" : $"bed-{show}";
        if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, $"{name}.*").FirstOrDefault() is { } made)
            return made;
        try
        {
            Report(job, "Making the music bed", 0.01);
            var track = await composer.GenerateAsync("[Instrumental]", BedStyle(show, episode.Mood), ct);
            if (track.Refused || track.Audio.Length == 0) return null;
            var path = Path.Combine(folder, $"{name}{(track.ContentType.Contains("wav", StringComparison.OrdinalIgnoreCase) ? ".wav" : ".mp3")}");
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

    /// <summary>
    /// What the music bed sounds like: light and instrumental, made to sit under a voice, with a steady beat the cuts
    /// can land on. A mood picked for the kind of video wins over the series' usual.
    /// </summary>
    internal static string BedStyle(Show show, string? mood = null) => mood switch
    {
        "energetic" => "Instrumental only, no vocals. An energetic, driving background track for an action video of sport or a party: punchy drums, bass and bright synths, about 125 bpm, a strong steady beat from start to end, made to sit under a narrator.",
        "calm" => "Instrumental only, no vocals. A calm, warm background track for a trip or a gentle how-to video: soft piano, light acoustic guitar and a gentle pulse, about 85 bpm, steady from start to end, made to sit quietly under a narrator.",
        "playful" => BedStyle(Show.Kids),
        _ => show switch
        {
            Show.Kids => "Instrumental only, no vocals. A light, bouncy, playful background track for a children's video: ukulele, glockenspiel, soft claps and pizzicato strings, about 110 bpm, cheerful and gentle, steady from start to end, made to sit quietly under a narrator.",
            Show.Robot => "Instrumental only, no vocals. A light, curious electronic background track for a science and robotics video: soft synth plucks, gentle beat, about 100 bpm, steady from start to end, made to sit quietly under a narrator.",
            _ => "Instrumental only, no vocals. A light, upbeat background track for a fun home video: acoustic guitar, light percussion and whistled hooks, about 105 bpm, steady from start to end, made to sit quietly under a narrator.",
        },
    };
}
