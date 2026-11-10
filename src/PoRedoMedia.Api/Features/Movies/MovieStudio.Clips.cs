using PoRedoMedia.Movie.Library;
using PoRedoMedia.Movie.Pictures;
using PoRedoMedia.Movie.Render;
using PoRedoMedia.Movie.Script;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Movies;

// A movie's clips: their order, their words and voices, and their pictures.
public sealed partial class MovieStudio
{
    private const int PicturesAtOnce = 3;

    /// <summary>One change that needs no service: it is applied, saved and can be undone.</summary>
    /// <returns>Why it could not be made, or null.</returns>
    public string? Apply(OpenMovie open, MovieEdit edit)
    {
        Guid Clip() => edit.ClipId ?? throw new ArgumentException("That change needs a clip.");
        try
        {
            return edit.Op switch
            {
                "rename" => edit.Text?.Trim() is { Length: > 0 } title ? Edit(open, e => EpisodeEditor.Rename(e, title)) : null,
                "number" => edit.Index is >= 1 and <= 999 and var number ? Edit(open, e => EpisodeEditor.SetNumber(e, number)) : "An episode number is 1 to 999.",
                "addClip" or "kind" or "removeVideo" => "Every clip is one of your own videos. To add one, start a new movie with it on the Films step, or put a video on a clip from its Picture tab.",
                "duplicateClip" => Duplicate(open, Clip()),
                "removeClip" => Edit(open, e => EpisodeEditor.RemoveClip(e, Clip())),
                "clipTitle" => edit.Text?.Trim() is { Length: > 0 } title ? Edit(open, e => EpisodeEditor.SetClipTitle(e, Clip(), title)) : null,
                "visualPrompt" => edit.Text?.Trim() is { Length: > 0 } prompt ? Edit(open, e => EpisodeEditor.SetVisualPrompt(e, Clip(), prompt)) : null,
                "reorder" => edit.Order is { } order && order.Length == open.Episode.Clips.Count ? Edit(open, e => EpisodeEditor.Reorder(e, order)) : null,
                "hostVisible" => Edit(open, e => EpisodeEditor.SetHostVisible(e, Clip(), edit.Flag == true)),
                "useTake" => Edit(open, e => EpisodeEditor.UseEarlierTake(e, Clip(), edit.Index ?? 0)),
                "robotView" => Edit(open, e => EpisodeEditor.SetRobotView(e, Clip(), edit.Flag == true)),
                "captions" => edit.Captions is { } c && Enum.TryParse<CaptionPreset>(c.Preset, out var preset) && AssCaptions.IsHexColour(c.Accent)
                    ? Edit(open, e => e with { Captions = new CaptionStyle(preset, Math.Clamp(c.FontSize, 32, 96), c.Accent.ToUpperInvariant(), Math.Clamp(c.Stroke, 0, 10)) })
                    : "The accent colour is typed as # and six letters or digits, such as #22CC88.",
                "removeMusic" => Edit(open, e => EpisodeEditor.SetMusic(e, null)),
                "memes" => Edit(open, e => EpisodeEditor.SetMemes(e, Clip(), edit.Flag != false)),
                "seriesLook" => UseSeriesLook(open),
                _ => "That change is not known.",
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return e.Message;
        }
    }

    private string? Duplicate(OpenMovie open, Guid clipId)
    {
        if (open.Episode.Clips.FirstOrDefault(c => c.Id == clipId) is not { } clip) return null;
        var copy = EpisodeEditor.Copy(clip);
        // The copy says the same words, so it is given the same recordings and not recorded, and paid for, again.
        EpisodeBuilder.CopyRecordings(clip.Id, copy.Id, open.Folder);
        return Edit(open, e => EpisodeEditor.AddClip(e, clipId, copy));
    }

    private string? UseSeriesLook(OpenMovie open)
    {
        var show = BeatPlan.ShowOf(open.Episode.Structure);
        var kit = SeriesKit.Load(KitFolder(show), show);
        var music = SeriesKit.PathOf(KitFolder(show), kit.Music) is { } track ? Import(track, open.Folder, "music-" + Path.GetFileName(track)) : null;
        return Edit(open, e => EpisodeEditor.UseSeriesLook(e, kit, music));
    }

    /// <summary>True when the movie's look, captions or outro music are no longer the series' own.</summary>
    private bool OffSeriesLook(OpenMovie open)
    {
        var show = BeatPlan.ShowOf(open.Episode.Structure);
        var kit = SeriesKit.Load(KitFolder(show), show);
        return EpisodeEditor.UseSeriesLook(open.Episode, kit, open.Episode.MusicPath) != open.Episode
            || (kit.Music is { } music && SeriesKit.PathOf(KitFolder(show), music) is not null && Path.GetFileName(open.Episode.MusicPath ?? "") != "music-" + music);
    }

    /// <summary>
    /// Puts a clip on another depth. The words for a depth are written the first time it is picked, which takes a few
    /// seconds and a small request.
    /// </summary>
    public async Task<string?> PickTierAsync(OpenMovie open, Guid clipId, string tierName, CancellationToken ct)
    {
        if (!Enum.TryParse<Tier>(tierName, out var tier)) return "That depth is not known.";
        if (open.Episode.Clips.FirstOrDefault(c => c.Id == clipId) is not { } clip) return null;
        if (clip.Scripts.ContainsKey(tier)) return Edit(open, e => EpisodeEditor.SetTier(e, clipId, tier));
        SpendIn(open.Folder);
        try
        {
            var script = await Writer.WriteTierAsync(open.Episode.Topic, clip, tier, ct);
            return Edit(open, e => e.Clips.Any(c => c.Id == clipId) ? EpisodeEditor.SetTier(EpisodeEditor.AddTier(e, clipId, tier, script), clipId, tier) : e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Plain(e);
        }
    }

    /// <summary>
    /// Changes what a clip says. The script model is asked whether the change moves the clip away from its picture
    /// (which then shows as out of date), and the new words are recorded straight away, at the usual voice cost.
    /// </summary>
    public async Task<string?> SetDialogueAsync(OpenMovie open, Guid clipId, string draft, CancellationToken ct)
    {
        if (open.Episode.Clips.FirstOrDefault(c => c.Id == clipId) is not { } clip) return null;
        draft = draft.Trim();
        if (draft.Length == 0 && !clip.OwnSound) return "Dialogue cannot be empty. The clip keeps its last words.";
        if (draft == clip.Active.Dialogue) return null;
        SpendIn(open.Folder);
        try
        {
            var updated = await EpisodeEditor.EditDialogueAsync(open.Episode, clipId, draft, Writer, ct);
            var changed = updated.Clips.First(c => c.Id == clipId);
            open.Notes.TryRemove(clipId, out _);
            // Only the words and the out-of-date mark are taken, so a change made to the clip meanwhile is kept.
            if (Edit(open, e => EpisodeEditor.SetDialogue(e, clipId, changed.Active.Dialogue, changed.Visual.Stale)) is { } problem) return problem;
            if (open.Episode.Clips.FirstOrDefault(c => c.Id == clipId) is { } now && (now.OwnSound || now.Active.Dialogue.Length > 0))
                await engine.Builder.NarrateClipAsync(now, open.Folder, ct);
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Plain(e);
        }
    }

    /// <summary>Has the script model rewrite a clip's words to answer the editor's note on them.</summary>
    public async Task<(string? Error, string? Status)> AnswerNoteAsync(OpenMovie open, Guid clipId, CancellationToken ct)
    {
        if (open.Episode.Clips.FirstOrDefault(c => c.Id == clipId) is not { } clip || !open.Notes.TryGetValue(clipId, out var note)) return (null, null);
        SpendIn(open.Folder);
        try
        {
            var revised = await Writer.ReviseAsync(clip, note, ct);
            return await SetDialogueAsync(open, clipId, revised, ct) is { } problem
                ? (problem, null)
                : (null, $"“{clip.Title}” was rewritten to answer the editor's note. Undo puts the old words back.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (Plain(e), null);
        }
    }

    /// <summary>Records every clip's voice, a few at a time. Lines already recorded are found and not paid for again.</summary>
    public string? NarrateAll(OpenMovie open) =>
        Start("Recording the voices", open.Id, open.Folder, (job, ct) => NarrateEveryClipAsync(job, open, ct),
            "Voices recorded. Clip lengths and the running time are now measured, not estimated.");

    /// <summary>
    /// Puts one of the user's Library videos on a clip. The clip's words are rewritten and recorded to fit the film's
    /// length, with the film's own sound turned down underneath.
    /// </summary>
    public string? AttachFilm(OpenMovie open, Guid clipId, Guid mediaId)
    {
        if (!engine.FfmpegAvailable) return "FFmpeg is needed to use a film, and it was not found on this server.";
        if (open.Episode.Clips.All(c => c.Id != clipId)) return "This clip is no longer in the movie.";
        return Start("Fitting the narration to your video", open.Id, open.Folder, async (job, ct) =>
        {
            var (path, _) = await StageAsync(mediaId, ct);
            var length = await engine.Builder.ProbeDurationAsync(path, ct);
            if (length < Conformance.MinFootage || length > Conformance.MaxFootage)
                throw new InvalidOperationException("Footage must be between 5 seconds and 1 minute long.");
            var clip = open.Episode.Clips.First(c => c.Id == clipId) with { OwnSound = false };
            Report(job, "Rewriting and recording the words until they fit", 0.2);
            var (copy, fit) = await ImportFootageAsync(clip, path, length, open.Folder, ct);
            if (Edit(open, e => EpisodeEditor.AttachVideo(e, clipId, copy, fit)) is { } problem) throw new IOException(problem);
            job.Message = fit.WithinTolerance
                ? FormattableString.Invariant($"Video {length:m\\:ss} · narration fitted to {fit.Duration.TotalSeconds:0.0} s.")
                : FormattableString.Invariant($"Closest fit is {Math.Abs(fit.Gap.TotalSeconds):0.0} s too {(fit.Gap < TimeSpan.Zero ? "short" : "long")}. Edit the dialogue to close the gap.");
        });
    }

    /// <summary>
    /// Says whether a clip's film plays with its own sound alone, or is narrated over. Keeping its own sound is free;
    /// narrating it rewrites the words to fit the film and records them.
    /// </summary>
    public async Task<string?> SetOwnSoundAsync(OpenMovie open, Guid clipId, bool own, CancellationToken ct)
    {
        if (open.Episode.Clips.FirstOrDefault(c => c.Id == clipId) is not { Visual.UserVideoPath: { } film } clip || clip.OwnSound == own) return null;
        if (own)
        {
            if (Edit(open, e => EpisodeEditor.SetOwnSound(e, clipId, true)) is { } problem) return problem;
            try
            {
                await engine.Builder.NarrateClipAsync(open.Episode.Clips.First(c => c.Id == clipId), open.Folder, ct);
                return null;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Plain(e);
            }
        }
        return Start("Fitting the narration to your video", open.Id, open.Folder, async (job, ct2) =>
        {
            var length = await engine.Builder.ProbeDurationAsync(film, ct2);
            if (length < Conformance.MinFootage)
                throw new InvalidOperationException("This film is under 5 seconds, too short to narrate over, so it keeps its own sound.");
            var seed = new[] { clip.Active.Dialogue, clip.Active.VisualPrompt, clip.Title }.First(t => !string.IsNullOrWhiteSpace(t));
            var narrated = EpisodeEditor.WithDialogue(clip with { OwnSound = false }, seed);
            Report(job, "Rewriting and recording the words until they fit", 0.2);
            var (copy, fit) = await ImportFootageAsync(narrated, film, length, open.Folder, ct2);
            if (Edit(open, e => EpisodeEditor.AttachVideo(EpisodeEditor.SetOwnSound(e, clipId, false), clipId, copy, fit)) is { } problem) throw new IOException(problem);
        });
    }

    // ---- Pictures ----

    private Visuals? VisualsAt(OpenMovie open, Quality quality) => engine.VisualsFor(open.Folder, Host, quality, Writer);

    private CostEstimate EstimateFor(OpenMovie open, Quality quality, bool finals = false) =>
        CostEstimate.For(open.Episode, engine.ImageModel, quality, finals, subjectSheet: VisualsAt(open, quality)?.NeedsSubjectSheet(open.Episode) == true);

    /// <summary>What generating every missing picture would make and cost, to show before it is agreed to.</summary>
    /// <param name="finals">The draft pictures drawn again at full quality, not the missing ones.</param>
    public MovieEstimateDto PictureEstimate(OpenMovie open, bool finals)
    {
        var quality = finals ? Quality.Medium : Quality;
        var estimate = EstimateFor(open, quality, finals);
        var summary = finals
            ? $"{estimate.Summary} Each draft picture is drawn again at full quality, from the same description, for the finished video."
            : $"{estimate.Summary}{(IsDraft(quality) ? " These are draft-quality pictures." : "")} Pictures you already have are reused at no cost.";
        return new(estimate.NothingToDo ? estimate.Summary : summary, estimate.Dollars, estimate.NothingToDo, estimate.NothingToDo ? null : OverBudget(estimate.Dollars ?? 0));
    }

    /// <summary>What this movie has spent on pictures and AI video so far.</summary>
    private static decimal PictureSpending(string folder) =>
        SpendLog.Breakdown(folder).Where(line => line.What is SpendLog.Picture or SpendLog.AiVideo).Sum(line => line.Dollars);

    /// <summary>
    /// Draws every picture the movie is missing (or, with <paramref name="finals"/>, every draft again at full
    /// quality), a few at a time. It holds to the cost that was shown: a run that would spend more stops there.
    /// </summary>
    public string? GenerateAll(OpenMovie open, bool finals)
    {
        if (!engine.PicturesAvailable) return "No picture service is configured on this server.";
        var quality = finals ? Quality.Medium : Quality;
        var estimate = EstimateFor(open, quality, finals);
        if (estimate.NothingToDo) return estimate.Summary;
        if (OverBudget(estimate.Dollars ?? 0) is { } refusal) return refusal;
        return Start(finals ? "Drawing the final pictures" : "Generating pictures", open.Id, open.Folder, async (job, ct) =>
        {
            var (made, halted, failures) = (0, "", new List<string>());
            try
            {
                (made, halted, failures) = await DrawAllAsync(job, open, estimate, quality, ct);
            }
            finally
            {
                job.Message = finals
                    ? $"Final pictures are ready for {Count(made, "clip")}. Build the preview again to watch them.{halted}"
                    : $"Pictures are ready for {Count(made, "clip")}.{halted}";
            }
            if (failures.Count > 0)
                job.Error = $"{Count(failures.Count, "clip")} kept {(failures.Count == 1 ? "its" : "their")} title card. {string.Join(" ", failures)}";
        });
    }

    private static string Count(int n, string thing) => n == 1 ? $"1 {thing}" : $"{n} {thing}s";

    private async Task<(int Made, string Halted, List<string> Failures)> DrawAllAsync(Job job, OpenMovie open, CostEstimate estimate, Quality quality, CancellationToken ct)
    {
        var visuals = VisualsAt(open, quality)!;
        // The subject's sheet first, alone: every picture drawn from it waits for it.
        if (estimate.ClipIds.Any(id => open.Episode.Clips.FirstOrDefault(c => c.Id == id) is { } c && Visuals.UsesSubject(c.Visual.Kind)))
        {
            Report(job, "Drawing the subject every picture is drawn from", 0.02);
            await visuals.SubjectSheetAsync(open.Episode, ct);
        }
        var before = PictureSpending(open.Folder);
        var (drawing, made, done, left) = (0m, 0, 0, 0);
        string? halted = null;
        var failures = new List<string>();
        var gate = new Lock();
        await Batch.EachAsync(estimate.ClipIds, PicturesAtOnce, async id =>
        {
            ct.ThrowIfCancellationRequested();
            if (open.Episode.Clips.FirstOrDefault(c => c.Id == id) is not { } clip) return;
            var price = CostEstimate.PriceOf(clip, engine.ImageModel, quality);
            lock (gate)
            {
                // What is being drawn now counts as spent, so three at once cannot each think they are the last allowed.
                if (halted is null && !estimate.Allows(PictureSpending(open.Folder) - before + drawing, price))
                    halted = $": the next picture would have taken the spending past the {SpendLog.Money(estimate.Dollars ?? 0)} you agreed to. Generate pictures again to see what the rest cost.";
                if (halted is null && OverBudget(price + drawing) is { } refusal)
                    halted = ". " + refusal;
                if (halted is not null)
                {
                    left++;
                    return;
                }
                drawing += price;
            }
            try
            {
                var problem = await GenerateOneAsync(open, id, anotherTake: false, quality, ct);
                lock (gate)
                {
                    if (problem is null) made++;
                    else failures.Add($"{clip.Title}: {problem}");
                }
            }
            finally
            {
                lock (gate) drawing -= price;
                Report(job, $"Drawn {Interlocked.Increment(ref done)} of {estimate.ClipIds.Count} · {clip.Title}", 0.05 + 0.95 * done / estimate.ClipIds.Count);
            }
        }, ct);
        var suffix = halted is null ? "" : $" The last {(left == 1 ? "clip keeps its title card" : $"{left} clips keep their title cards")}{halted}";
        return (made, suffix, failures);
    }

    /// <summary>Draws one clip's picture now, or another take of it. Asked for by hand, one at a time.</summary>
    public async Task<string?> GeneratePictureAsync(OpenMovie open, Guid clipId, bool anotherTake, CancellationToken ct)
    {
        if (!engine.PicturesAvailable) return "No picture service is configured on this server.";
        if (IsWorking) return Busy;
        if (open.Episode.Clips.FirstOrDefault(c => c.Id == clipId) is not { } clip) return "This clip is no longer in the movie.";
        if (OverBudget(CostEstimate.PriceOf(clip, engine.ImageModel, Quality)) is { } refusal) return refusal;
        if (!await open.PictureGate.WaitAsync(0, ct)) return "Another picture is being drawn. Wait for it, then ask again.";
        SpendIn(open.Folder);
        try
        {
            return await GenerateOneAsync(open, clipId, anotherTake, Quality, ct);
        }
        finally
        {
            open.PictureGate.Release();
        }
    }

    /// <returns>Null when the clip has its picture, or why it has not.</returns>
    private async Task<string?> GenerateOneAsync(OpenMovie open, Guid clipId, bool anotherTake, Quality quality, CancellationToken ct)
    {
        if (open.Episode.Clips.FirstOrDefault(c => c.Id == clipId) is not { } clip) return "This clip is no longer in the movie.";
        if (anotherTake) clip = EpisodeEditor.NextTake(clip);
        var asked = DateTime.UtcNow;
        try
        {
            var visuals = VisualsAt(open, quality)!;
            var episode = open.Episode;
            var sheet = Visuals.UsesSubject(clip.Visual.Kind) ? await visuals.SubjectSheetAsync(episode, ct) : null;
            var (drawn, paths, change) = await visuals.DrawOrSubstituteAsync(clip, episode.Look, ct, sheet);
            foreach (var video in paths.Where(p => EpisodeBuilder.VideoExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase) && !File.Exists(Clip.PosterFor(p))))
                await engine.Builder.PosterAsync(video, ct);
            await CheckPictureAsync(drawn, paths, asked, ct);
            var draft = IsDraft(quality);
            if (change is null)
                return Edit(open, e => EpisodeEditor.ApplyPicture(e, clip, paths, draft));
            Note(change);
            return Edit(open, e => EpisodeEditor.ApplySubstitute(e, clip, drawn, paths, draft));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return "The picture service took too long to answer.";
        }
        catch (Exception e)
        {
            return Plain(e);
        }
    }

    /// <summary>
    /// Looks at a picture just drawn for what picture models get wrong: a host unlike its design, and lettering (they
    /// misspell). What is found is said, not acted on. A picture found saved was looked at when it was made.
    /// </summary>
    private async Task CheckPictureAsync(Clip clip, IReadOnlyList<string> paths, DateTime asked, CancellationToken ct)
    {
        try
        {
            var fresh = paths.Where(p => Path.GetExtension(p).Equals(".png", StringComparison.OrdinalIgnoreCase) && File.GetCreationTimeUtc(p) >= asked).ToList();
            var sheet = Path.Combine(Host, "sheet.png");
            if (clip is { HostVisible: true, Visual.Kind: VisualKind.Still or VisualKind.MultiPanel } && File.Exists(sheet) && fresh.FirstOrDefault() is { } picture
                && await Writer.CheckHostAsync(picture, sheet, ct) is { } difference)
                Note($"The host in “{clip.Title}” differs from its design: {difference} Another take may put it right.");
            if (clip.Visual.Kind is VisualKind.Still or VisualKind.MultiPanel or VisualKind.Parallax)
                foreach (var drawn in fresh)
                    if (await Writer.CheckLetteringAsync(drawn, ct) is { } lettering)
                    {
                        Note($"The picture for “{clip.Title}” has lettering drawn into it (“{lettering}”). Another take may leave it out.");
                        break;
                    }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Looking is a help only.
        }
    }
}
