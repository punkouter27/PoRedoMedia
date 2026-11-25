using System.Globalization;
using PoRedoMedia.Movie.Library;
using PoRedoMedia.Movie.Render;
using PoRedoMedia.Movie.Script;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Movies;

// Films handed over without a word about them, made into a sensible video: each is watched and listened to, the kind
// of video they make is decided (and from it the series, pace, music, narrator and roast), the best parts of each are
// kept, put in an order that tells the story, cut at the movie's shape and made to match, and narrated or left to
// speak for themselves by what is heard in them. The script is then written for those parts, in that order.
public sealed partial class MovieStudio
{
    /// <summary>What was decided about a movie made from films, and the parts it is made of, in the order they are shown.</summary>
    /// <param name="Request">The request as it is carried out: in Auto, with the series, roast and narrator the app chose.</param>
    /// <param name="Line">The plan in one line, shown while the movie is made and with the finished video.</param>
    internal sealed record CutPlan(VideoKind Kind, Show Show, BeatStructure Structure, string Mood, Shape Shape, MovieLessonRequest Request,
        IReadOnlyList<FootageNote> Parts, string Line);

    private sealed record Watched(MovieFilmDto Film, FootageNote Note, FilmFacts Facts, bool HasSound, double Luma = 0);

    /// <summary>
    /// Seconds of clips a Short holds: under a minute with the intro, the outro and the slow replay (about 15 seconds
    /// together), or without them when the series has no channel name.
    /// </summary>
    private const double ShortSeconds = 40, ShortSecondsNoBookends = 52;

    /// <summary>Names the parts cut from films on disk. Change it when how a part is cut changes, so parts are cut again.</summary>
    private const string CutVersion = "cut 1";

    private const int WatchAtOnce = 3, CutAtOnce = 2;

    private async Task<CutPlan> PlanCutAsync(Job job, MovieLessonRequest request, CancellationToken ct)
    {
        var chosen = !request.Auto && TryStructure(request, out var picked) ? picked : BeatStructure.Movie;
        // Before the kind is known, a film is watched as a film about anything.
        var watchShow = request.Auto ? Show.General : BeatPlan.ShowOf(chosen);
        Writer.Show = watchShow;

        Report(job, "Fetching your films", 0);
        var films = new List<Watched>();
        foreach (var film in request.Films.DistinctBy(f => f.MediaId))
        {
            var note = await NoteAsync(film, ct);
            var (about, hasSound) = await FactsAsync(note.Path, ct);
            films.Add(new Watched(film, note, about, hasSound));
        }

        Report(job, $"Watching and listening to {Count(films.Count, "film")}", 0.1);
        var watched = new Watched[films.Count];
        var done = 0;
        await Batch.EachAsync(Enumerable.Range(0, films.Count), WatchAtOnce, async i =>
        {
            var film = films[i];
            watched[i] = film with { Note = await UnderstandAsync(film.Note, watchShow, ct, hearAll: true), Luma = await LumaAsync(film.Note.Path, ct) };
            Report(job, $"Watched {Interlocked.Increment(ref done)} of {films.Count} films", 0.1 + 0.5 * done / films.Count);
        }, ct);
        films = [.. watched];

        // ---- What kind of video, and so how it is told ----
        var kind = VideoKinds.Of(chosen);
        VideoPlan? plan = null;
        if (request.Auto)
        {
            Report(job, "Deciding what kind of video your films make", 0.62);
            try
            {
                plan = await Writer.PlanVideoAsync([.. films.Select(Brief)], ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Note($"What kind of video the films make could not be decided, so it is made as an everyday one. {Plain(e)}");
            }
            kind = VideoKinds.Parse(plan?.Kind) ?? VideoKind.Everyday;
        }
        var (show, structure) = request.Auto ? VideoKinds.SeriesOf(kind) : (BeatPlan.ShowOf(chosen), chosen);
        var pace = VideoKinds.ParsePace(plan?.Pace) ?? VideoKinds.PaceOf(kind);
        var mood = plan?.Mood is { } said && VideoKinds.Moods.Contains(said) ? said : VideoKinds.MoodOf(kind);
        var asked = request.Auto ? Decided(request, show, structure, pace, plan) : request;

        // ---- Which parts of which films ----
        var best = request.Cut == MovieLessonRequest.BestParts;
        var candidates = new List<(int Film, Part Part)>();
        for (var i = 0; i < films.Count; i++)
        {
            var (note, seen) = (films[i].Note, films[i].Note.Seen);
            candidates.AddRange(SmartCut.Choose(seen?.Stretches, note.Length.TotalSeconds, pace, seen?.Highlight, note.Heard, whole: !best).Select(p => (i, p)));
        }
        var upright = films.Count(f => f.Facts.Portrait) * 5 >= films.Count * 3;
        var bookends = SeriesKit.Load(KitFolder(show), show).ChannelName.Trim().Length > 0;
        var shortSeconds = bookends ? ShortSeconds : ShortSecondsNoBookends;
        var format = request.Format ?? (request.Auto ? MovieLessonRequest.AutoFormat : MovieLessonRequest.FullFormat);
        var isShort = format == MovieLessonRequest.ShortFormat
            || (format == MovieLessonRequest.AutoFormat && upright && candidates.Sum(c => c.Part.Length) <= shortSeconds * 1.5);
        var kept = SmartCut.Fit(candidates, c => c.Part.Length, c => c.Part.Score, isShort ? shortSeconds : Durations.MaxMovie.TotalSeconds - 20, MostFilms);

        // ---- In what order, and which films are one scene ----
        var used = kept.Select(k => k.Film).Distinct().ToList();
        var facts = used.Select(i => films[i].Facts).ToList();
        var order = Arrange.Order(facts, [.. used.Select(i => kept.Where(k => k.Film == i).Max(k => k.Part.Score))], VideoKinds.InTimeOrder(kind));
        var (scene, _) = Arrange.Scenes(facts);
        var shown = order.SelectMany(o => kept.Where(k => k.Film == used[o]).Select(k => (k.Film, k.Part, Scene: scene[o]))).ToList();

        // ---- Each part cut at the movie's shape, steadied and brightened to match ----
        var shape = isShort ? Shape.Tall : Shape.Wide;
        var lumas = films.Select(f => f.Luma).Where(l => l > 0).Order().ToList();
        var target = lumas.Count == 0 ? 110 : Math.Clamp(lumas[lumas.Count / 2], 90, 130);
        Report(job, $"Cutting {Count(shown.Count, "part")} to fit together", 0.7);
        var parts = new FootageNote[shown.Count];
        done = 0;
        await Batch.EachAsync(Enumerable.Range(0, shown.Count), CutAtOnce, async j =>
        {
            var (film, part, inScene) = shown[j];
            parts[j] = await CutPartAsync(films[film], part, shape, target, inScene, ct);
            Report(job, $"Cut {Interlocked.Increment(ref done)} of {shown.Count} parts", 0.7 + 0.3 * done / shown.Count);
        }, ct);

        var left = films.Count - used.Count;
        var total = TimeSpan.FromSeconds(parts.Sum(p => p.Length.TotalSeconds));
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{VideoKinds.Label(kind)} video, about {total:m\\:ss}: {Count(parts.Length, best ? "moment" : "film")}{(best ? $" from {Count(films.Count, "film")}" : "")}, {mood} music, {(isShort ? "an upright Short" : "a full video")}.")
            + (plan?.Line is { Length: > 0 } what ? $" {what.TrimEnd('.')}." : "")
            + (left > 0 ? $" {Count(left, "film")} did not make the cut." : "");
        return new CutPlan(kind, show, structure, mood, shape, asked, parts, line);
    }

    /// <summary>The request with what the app decided in Auto: the series and shape, the roast and the narrator.</summary>
    private static MovieLessonRequest Decided(MovieLessonRequest request, Show show, BeatStructure structure, Pace pace, VideoPlan? plan)
    {
        // A kids' video keeps the owner's own kids' setting (the Brutal roast and a laugh track, in the Comic voice).
        var mix = show == Show.Kids ? MemeMix.Kids : MemeMix.Default.WithRoast(plan?.Roast ?? 2);
        var off = show != Show.Kids && plan?.Roast == 0;
        var style = NarratorChoice.Styles.Any(s => s.Id == plan?.Narrator) ? plan!.Narrator : show == Show.Kids ? "excited" : "warm";
        var narrator = new NarratorChoice(style, pace switch { Pace.Fast => 3, Pace.Calm => 1, _ => 2 });
        return request with
        {
            Show = show.ToString(),
            Structure = structure.ToString(),
            RoastVoice = off ? "Off" : show == Show.Kids ? "Comic" : null,
            Mix = mix.Encode(),
            CrudeRoast = mix.Crude,
            Narrator = narrator.Encode(),
        };
    }

    private static FilmBrief Brief(Watched film) => new(
        new[] { film.Note.Note, film.Note.Seen?.Note }.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim() ?? "",
        film.Note.Seen?.Timeline ?? "(not watched)",
        film.Note.Length.TotalSeconds,
        SmartCut.Talking(film.Note.Heard, 0, film.Note.Length.TotalSeconds),
        film.Facts.Portrait);

    /// <summary>
    /// One part of a film as the movie uses it: a film of its own at the movie's shape (the film itself when it is
    /// used whole and needs nothing done), with what was seen and heard in that part, and whether it keeps its own sound.
    /// </summary>
    private async Task<FootageNote> CutPartAsync(Watched film, Part part, Shape shape, double targetLuma, int scene, CancellationToken ct)
    {
        var (note, seen) = (film.Note, film.Note.Seen);
        var whole = part.Start <= 0.05 && part.End >= note.Length.TotalSeconds - 0.05;
        var graph = film.Facts.Width > 0
            ? FfmpegArgs.Framing(film.Facts.Width, film.Facts.Height, shape, seen?.SubjectX ?? 0.5, part.Shaky, film.Luma > 0 ? FfmpegArgs.Brightness(film.Luma, targetLuma) : 0, always: !whole)
            : whole ? null : "[0:v]fps=30,setsar=1,format=yuv420p[v]";
        var path = note.Path;
        var length = note.Length;
        if (graph is not null)
        {
            var file = new FileInfo(note.Path);
            var key = Files.TextHash(FormattableString.Invariant($"{CutVersion}|{file.FullName}|{file.Length}|{part.Start:0.###}|{part.End:0.###}|{graph}"))[..16];
            path = Path.Combine(Staging, "parts", $"{Path.GetFileNameWithoutExtension(note.Path)}-{key}.mp4");
            if (!File.Exists(path))
            {
                var scratch = Files.NewScratchFile(".mp4");
                try
                {
                    await engine.Runner.RunAsync(FfmpegArgs.Part(note.Path, part.Start, part.Length, graph, film.HasSound, scratch), Path.GetDirectoryName(scratch)!,
                        TimeSpan.FromSeconds(part.Length), null, ct);
                    Files.EnsureFolderFor(path);
                    File.Move(scratch, path, overwrite: true);
                }
                finally
                {
                    File.Delete(scratch);
                }
            }
            length = await engine.Builder.ProbeDurationAsync(path, ct);
        }
        var heard = SmartCut.Slice(note.Heard, part.Start, part.End);
        // People talking keep their own words; quiet, noise or nothing said is narrated over. The owner's own choice
        // of the film's sound always stands, and a part too short to narrate keeps its sound.
        var own = film.Film.OwnSound || length < Conformance.MinFootage || SmartCut.Talking(note.Heard, part.Start, part.End);
        return new FootageNote(path, note.Note, own, length)
        {
            Seen = seen is null ? null : new FootageSeen(SmartCut.Slice(seen.Timeline, part.Start, part.End), seen.Note,
                Math.Clamp(seen.Highlight - part.Start, 0, length.TotalSeconds), null, seen.SubjectX),
            Heard = heard is { Count: > 0 } ? heard : null,
            Scene = scene,
            Score = part.Score,
        };
    }

    /// <summary>A film's size as shown, when and where it was filmed, and whether it has sound. Unknown facts are left out.</summary>
    private async Task<(FilmFacts Facts, bool HasSound)> FactsAsync(string film, CancellationToken ct)
    {
        try
        {
            var json = await engine.Runner.ProbeAsync(FfmpegArgs.Facts(film), ct);
            return (FfmpegArgs.ReadFacts(json), json.Contains("\"audio\"", StringComparison.Ordinal));
        }
        catch (Exception e) when (e is FfmpegException or IOException or System.Text.Json.JsonException)
        {
            return (new FilmFacts(0, 0, null, null), true);
        }
    }

    /// <summary>How bright a film is on average (0 to 255); 0 when it could not be measured. Free, and kept by the film.</summary>
    private async Task<double> LumaAsync(string film, CancellationToken ct)
    {
        try
        {
            var kept = await FootageCache.GetOrCreateAsync(["luma", Path.GetFileName(film), new FileInfo(film).Length.ToString(CultureInfo.InvariantCulture)], ".txt", async scratch =>
            {
                var work = Files.NewScratchFolder("luma");
                try
                {
                    await engine.Runner.RunAsync(FfmpegArgs.Luma(film, "luma.txt"), work, null, null, ct);
                    var values = FfmpegArgs.MetadataValues(await File.ReadAllTextAsync(Path.Combine(work, "luma.txt"), ct), "lavfi.signalstats.YAVG");
                    await File.WriteAllTextAsync(scratch, (values.Count > 0 ? values.Average() : 0).ToString(CultureInfo.InvariantCulture), ct);
                }
                finally
                {
                    DeleteFolder(work);
                }
            });
            return double.TryParse(await File.ReadAllTextAsync(kept, ct), NumberStyles.Float, CultureInfo.InvariantCulture, out var luma) ? luma : 0;
        }
        catch (Exception e) when (e is FfmpegException or IOException)
        {
            return 0;
        }
    }

    // ---- Looking over the finished clips before the final render ----

    /// <summary>Clips found mostly black are left out: more than this share of their frames.</summary>
    private const double MostlyBlack = 0.4;

    /// <summary>
    /// Looks over every clip as Meme-ify left it, before the movie is joined: frames that are nearly black are counted
    /// (free), and one frame from each is shown to a model once for a clip that is plainly unusable (the lens covered,
    /// a pocket). Each such clip is taken out of the movie (undo puts it back), never more than a third of them.
    /// </summary>
    /// <returns>A line for each clip left out.</returns>
    private async Task<List<string>> CheckClipsAsync(Job job, OpenMovie open, CancellationToken ct)
    {
        var said = new List<string>();
        if (!open.Episode.AutoEdit || open.Episode.Clips.Count < 2) return said;
        Report(job, "Looking over the finished clips", 0.98);
        var episode = WithMemes(open);
        var faults = new Dictionary<Guid, string>();
        var frames = new List<(int Clip, string Path)>();
        var work = Files.NewScratchFolder("check");
        try
        {
            for (var i = 0; i < episode.Clips.Count; i++)
            {
                if (episode.Clips[i].Visual.UserVideoPath is not { } film || !File.Exists(film)) continue;
                try
                {
                    var length = await engine.Runner.ProbeDurationAsync(film, ct);
                    var marks = Path.Combine(work, $"black{i}.txt");
                    await engine.Runner.RunAsync(FfmpegArgs.Black(film, Path.GetFileName(marks)), work, null, null, ct);
                    var black = File.Exists(marks) ? FfmpegArgs.MetadataValues(await File.ReadAllTextAsync(marks, ct), "lavfi.blackframe.pblack").Count : 0;
                    if (length.TotalSeconds > 0 && black / (length.TotalSeconds * 5) > MostlyBlack)
                        faults[open.Episode.Clips[i].Id] = "it is mostly black";
                    var frame = Path.Combine(work, $"frame{i}.jpg");
                    await engine.Runner.RunAsync(FfmpegArgs.Still(film, length / 2, EpisodeBuilder.FrameWidth, frame), work, null, null, ct);
                    frames.Add((i + 1, frame));
                }
                catch (FfmpegException)
                {
                    // A clip that cannot be measured is not judged.
                }
            }
            try
            {
                foreach (var fault in await Writer.CheckClipsAsync(frames, ct))
                    faults.TryAdd(open.Episode.Clips[fault.Clip - 1].Id, fault.Problem.Trim().TrimEnd('.'));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Looking is a help only: the black frames were counted anyway.
            }
        }
        finally
        {
            DeleteFolder(work);
        }

        foreach (var (id, problem) in faults.Take(open.Episode.Clips.Count / 3))
            if (open.Episode.Clips.FirstOrDefault(c => c.Id == id) is { } clip && Edit(open, e => EpisodeEditor.RemoveClip(e, id)) is null)
                said.Add($"“{clip.Title}” was left out: {problem}. Undo puts it back.");
        return said;
    }

    // ---- Cuts on the beat ----

    /// <summary>
    /// Moves each cut of a movie made from films onto a beat of its music bed, by taking a fraction of a second off the
    /// end of the clip before it (a copy of the clip, so nothing is encoded again). Left as it is when the bed has no
    /// steady beat. Only the rendered copy is changed.
    /// </summary>
    private async Task<Episode> OnTheBeatAsync(Job job, OpenMovie open, Episode episode, CancellationToken ct)
    {
        if (episode.BedPath is not { } bed || !File.Exists(bed) || episode.Clips.Count < 2) return episode;
        var work = Files.NewScratchFolder("beat");
        try
        {
            Report(job, "Finding the beat of the music", 0.03);
            await engine.Runner.RunAsync(BeatGrid.Args(bed, "energy.txt"), work, null, null, ct);
            var energy = Path.Combine(work, "energy.txt");
            if (!File.Exists(energy) || BeatGrid.Find(BeatGrid.Read(await File.ReadAllTextAsync(energy, ct))) is not { } beat) return episode;
            var lengths = new List<double>();
            foreach (var clip in episode.Clips)
                lengths.Add(clip.Visual.UserVideoPath is { } film && File.Exists(film) ? (await engine.Runner.ProbeDurationAsync(film, ct)).TotalSeconds : 0);
            if (lengths.Any(l => l <= 0)) return episode;
            var bedLength = (await engine.Runner.ProbeDurationAsync(bed, ct)).TotalSeconds;
            var beats = BeatGrid.Beats(beat, bedLength, lengths.Sum());
            var trims = BeatGrid.Trims(lengths, FfmpegArgs.AudioCrossfade.TotalSeconds, beats, Math.Min(0.3, beat.Period / 2), 1.5);

            var clips = episode.Clips.ToList();
            var folder = Path.Combine(open.Folder, "showtime");
            for (var i = 0; i < clips.Count; i++)
            {
                if (trims[i] < 0.02) continue;
                var film = clips[i].Visual.UserVideoPath!;
                var keep = lengths[i] - trims[i];
                var key = Files.TextHash(FormattableString.Invariant($"{film}|{new FileInfo(film).Length}|{File.GetLastWriteTimeUtc(film).Ticks}|{keep:0.###}"))[..16];
                var cut = Path.Combine(folder, $"{key}-beat.mp4");
                if (!File.Exists(cut))
                {
                    Files.EnsureFolderFor(cut);
                    var scratch = Files.NewScratchFile(".mp4");
                    try
                    {
                        await engine.Runner.RunAsync(["-i", film, "-t", keep.ToString("0.###", CultureInfo.InvariantCulture), "-c", "copy", "-y", scratch], work, null, null, ct);
                        File.Move(scratch, cut, overwrite: true);
                    }
                    finally
                    {
                        File.Delete(scratch);
                    }
                }
                clips[i] = clips[i] with { Visual = clips[i].Visual with { UserVideoPath = cut } };
            }
            return episode with { Clips = clips };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            job.Detail = $"The cuts were left off the beat: {Plain(e)}";
            return episode;
        }
        finally
        {
            DeleteFolder(work);
        }
    }
}
