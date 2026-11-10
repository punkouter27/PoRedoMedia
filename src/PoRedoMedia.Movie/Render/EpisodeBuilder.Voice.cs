using System.Text.Json;

namespace PoRedoMedia.Movie.Render;

// Each clip's sound: its narration, or its film's own sound and the words heard in it, and how long it runs.
public sealed partial class EpisodeBuilder
{
    /// <summary>Narrates one clip's active dialogue. Audio is cached by its text, so an unchanged clip is never spoken twice.</summary>
    public async Task<Narration> NarrateClipAsync(Clip clip, string folder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = clip.Active.Dialogue;
        var audio = AudioPath(clip, text, folder);
        var words = Path.ChangeExtension(audio, ".words.json");

        // One narration at a time for each file. Two requests for the same line (an edit being applied while Play clip
        // is pressed) would otherwise both write it; the second now waits and finds the first one's result. Different
        // lines are recorded side by side.
        var gate = _narrationGates.GetOrAdd(Path.GetFullPath(audio), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(audio) && File.Exists(words))
                try
                {
                    if (Json.Read<List<WordTiming>>(await File.ReadAllTextAsync(words, ct)) is { } timings)
                        return await WithFilmUnderAsync(clip, new Narration(audio, WavInfo.Duration(audio), timings), ct);
                }
                catch (Exception e) when (e is JsonException or InvalidDataException)
                {
                    // A recording cut short (a crash, a sync conflict) is recorded again instead of failing every time.
                }

            Narration narration;
            if (OwnSound(clip) is { } film)
            {
                narration = await TakeSoundAsync(film, text, audio, ct);
            }
            else
            {
                narration = await narrator.SynthesizeAsync(text, audio, clip.NarrationRate, ct);
                if (PaceLearned is { } learned && clip.NarrationRate == 1.0 && Durations.Learned(Durations.WordsPerMinute, text, narration.Duration) is { } pace)
                    learned(Durations.WordsPerMinute = pace);
            }
            await File.WriteAllTextAsync(words, Json.Write(narration.Words), ct);
            return await WithFilmUnderAsync(clip, narration, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The film whose own sound plays quietly under the clip's narration; null when nothing plays under it.</summary>
    private static string? FilmUnder(Clip clip) =>
        clip is { OwnSound: false, Visual.UserVideoPath: { } film } && File.Exists(film) ? film : null;

    /// <summary>
    /// Where a narration laid over its film's sound is kept: beside the voice alone, named for the film as it is on
    /// disk, so a replaced film is laid under again while the voice, which cost money, is kept.
    /// </summary>
    private static string? UnderPath(Clip clip, string audio) =>
        FilmUnder(clip) is { } film
            ? Path.ChangeExtension(audio, $".{Files.TextHash(FormattableString.Invariant($"{VoiceOverMix}|{film}|{new FileInfo(film).Length}|{File.GetLastWriteTimeUtc(film).Ticks}"))[..8]}.under.wav")
            : null;

    /// <summary>Names the voice-over-on-film mix. Change it when <see cref="FfmpegArgs.VoiceOverFilm"/> changes, so clips are mixed again (free).</summary>
    private const string VoiceOverMix = "mix 2";

    // A narrated film keeps its own sound, turned down under the voice. Free: done here, from the recording already
    // made. A film with no sound track is narrated over silence, as before.
    private async Task<Narration> WithFilmUnderAsync(Clip clip, Narration narration, CancellationToken ct)
    {
        if (FilmUnder(clip) is not { } film || UnderPath(clip, narration.AudioPath) is not { } under)
            return narration;
        if (!File.Exists(under))
        {
            if (!await HasSoundAsync(film, ct)) return narration;
            // Made in the scratch folder and moved in whole, so a mix cut short is never taken for a finished one.
            var scratch = Files.NewScratchFile(".wav");
            try
            {
                await ffmpeg.RunAsync(FfmpegArgs.VoiceOverFilm(narration.AudioPath, film, scratch), Path.GetDirectoryName(scratch)!, null, null, ct);
                File.Move(scratch, under, overwrite: true);
            }
            finally
            {
                File.Delete(scratch);
            }
        }
        return narration with { AudioPath = under };
    }

    private async Task<bool> HasSoundAsync(string film, CancellationToken ct) =>
        (await ffmpeg.ProbeAsync(["-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", film], ct)).Trim().Length > 0;

    // ponytail: a gate is kept for every line spoken while the app runs, a few hundred small objects in a long day.
    // Taking one out once free would race a caller that has just been handed it; a count of waiters taken under a
    // lock would let each go when its last caller leaves, if a session ever grows that large.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _narrationGates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Told the voice's pace each time a recording changes what it is taken to be, so it can be kept for the next
    /// run. Null means the pace is not learned at all: the stand-in voice speaks at a pace of its own.
    /// </summary>
    public Action<double>? PaceLearned { get; init; }

    /// <summary>The film whose own sound the clip plays instead of narration; null for a narrated clip.</summary>
    private static string? OwnSound(Clip clip) =>
        clip is { OwnSound: true, Visual.UserVideoPath: { } film } && File.Exists(film) ? film : null;

    // The film's sound, cut or padded to the film's length, stands in for the narration: the timeline, the joins and
    // the captions then treat the clip like any other. Words typed for it are spread across it as captions. Free: no
    // service is asked for anything.
    private async Task<Narration> TakeSoundAsync(string film, string text, string audio, CancellationToken ct)
    {
        var length = await ffmpeg.ProbeDurationAsync(film, ct);
        var hasSound = await HasSoundAsync(film, ct);
        Files.EnsureFolderFor(audio);
        await ffmpeg.RunAsync(FfmpegArgs.FootageSound(hasSound ? film : null, length, audio), Path.GetDirectoryName(audio)!, null, null, ct);
        return new Narration(audio, WavInfo.Duration(audio), Heard(film, text, length) ?? WordTiming.Spread(text, length));
    }

    /// <summary>
    /// The words typed for a film, at the times they were heard said in it. Only when the typed words are as many as
    /// those heard: a word put right keeps its time, but words added or taken away leave nothing to match them to.
    /// </summary>
    private static IReadOnlyList<WordTiming>? Heard(string film, string text, TimeSpan length)
    {
        try
        {
            var heard = Json.Read<List<WordTiming>>(File.ReadAllText(Clip.HeardFor(film)));
            var typed = Durations.SplitWords(text);
            return heard is { Count: > 0 } && heard.Count == typed.Length
                ? [.. heard.Select((w, i) => w with { Text = typed[i] }).Where(w => w.Start < length)]
                : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // never listened to, or the file is damaged: the words are spread across the film
        }
    }

    /// <summary>
    /// Listens to what is said in a film, and gives the words with their times; empty when the film has no sound.
    /// Paid for by the second, so it is done only as part of something the owner asked for.
    /// </summary>
    public async Task<IReadOnlyList<WordTiming>> HearAsync(string film, Hear hear, CancellationToken ct)
    {
        if (!await HasSoundAsync(film, ct)) return [];
        var speech = Files.NewScratchFile(".wav");
        try
        {
            await ffmpeg.RunAsync(FfmpegArgs.SpeechSound(film, speech), Path.GetDirectoryName(speech)!, null, null, ct);
            return await hear(speech, ct);
        }
        finally
        {
            File.Delete(speech);
        }
    }

    /// <summary>
    /// A few frames spread across a film, small, for a model to look at. They go in a scratch folder of their own,
    /// which the caller deletes once they have been looked at.
    /// </summary>
    public async Task<IReadOnlyList<Frame>> FramesAsync(string film, int count, CancellationToken ct)
    {
        var length = await ffmpeg.ProbeDurationAsync(film, ct);
        var folder = Files.NewScratchFolder("frames");
        var frames = new List<Frame>(count);
        for (var i = 0; i < count; i++)
        {
            // From the middle of each stretch, so the first is past any fade up and the last before the film ends.
            var at = length * ((i + 0.5) / count);
            var path = Path.Combine(folder, $"{i:00}.jpg");
            await ffmpeg.RunAsync(FfmpegArgs.Still(film, at, FrameWidth, path), folder, null, null, ct);
            frames.Add(new Frame(at, path));
        }
        return frames;
    }

    /// <summary>Frames a model looks at are this wide: enough to see what happens, and few tokens.</summary>
    public const int FrameWidth = 512;

    private string AudioPath(Clip clip, string text, string folder)
    {
        // Own sound is keyed by the film as it is on disk, so a replaced film is taken again.
        var source = OwnSound(clip) is { } film
            ? FormattableString.Invariant($"own|{film}|{new FileInfo(film).Length}|{File.GetLastWriteTimeUtc(film).Ticks}")
            : FormattableString.Invariant($"{narrator.GetType().Name}|{clip.NarrationRate:0.###}");
        var key = Files.TextHash($"{source}|{text}")[..16];
        return Path.Combine(folder, "audio", $"{clip.Id:N}-{key}.wav");
    }

    /// <summary>
    /// Gives a copied clip the recordings of the clip it was copied from. They are named by the clip, so without this
    /// the copy would be recorded again, and paid for again, for the very same words.
    /// </summary>
    public static void CopyRecordings(Guid from, Guid to, string folder)
    {
        var audio = Path.Combine(folder, "audio");
        if (!Directory.Exists(audio)) return;
        foreach (var file in Directory.EnumerateFiles(audio, $"{from:N}-*"))
            File.Copy(file, Path.Combine(audio, $"{to:N}{Path.GetFileName(file)[32..]}"), overwrite: true);
    }

    /// <summary>How long the clip's narration really runs, once it has been recorded; null until then.</summary>
    public TimeSpan? MeasuredDuration(Clip clip, string folder)
    {
        var audio = AudioPath(clip, clip.Active.Dialogue, folder);
        try
        {
            // The word timings are written last, so their presence means the recording is whole.
            return File.Exists(Path.ChangeExtension(audio, ".words.json")) ? WavInfo.Duration(audio) : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return null;
        }
    }
}
