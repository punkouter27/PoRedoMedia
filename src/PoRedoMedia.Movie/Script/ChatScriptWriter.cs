using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PoRedoMedia.Movie.Script;

/// <summary>Sends one request and returns the model's reply, which must be JSON matching <paramref name="schema"/>.</summary>
/// <param name="onText">Given each piece of the reply as it arrives; null when nobody is watching.</param>
public delegate Task<string> JsonChat(string deployment, string system, string user, string schemaName, string schema, Action<string>? onText, CancellationToken ct);

/// <summary>The same, with pictures to look at, each after a line of text saying what it is.</summary>
public delegate Task<string> SeeingChat(string deployment, string system, string user, IReadOnlyList<(string Label, string Path)> pictures, string schemaName, string schema, CancellationToken ct);

/// <summary>Writes scripts with Azure OpenAI. The <see cref="JsonChat"/> seam lets tests supply replies without a network.</summary>
public sealed partial class ChatScriptWriter(AppSettings settings, JsonChat chat) : IScriptWriter
{
    // A reply with a field missing or null fails to read, so the records below can be trusted as written.
    private static readonly JsonSerializerOptions Replies = Json.Knowing(new()
    {
        PropertyNameCaseInsensitive = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    }, ReplyJson.Default);

    // Every shape a reply is read as.
    [JsonSerializable(typeof(OutlineDto))]
    [JsonSerializable(typeof(TierDto))]
    [JsonSerializable(typeof(ReviewDto))]
    [JsonSerializable(typeof(RewriteDto))]
    [JsonSerializable(typeof(DriftDto))]
    [JsonSerializable(typeof(TeasersDto))]
    [JsonSerializable(typeof(ShotsDto))]
    [JsonSerializable(typeof(SearchDto))]
    [JsonSerializable(typeof(HostDto))]
    [JsonSerializable(typeof(LetteringDto))]
    [JsonSerializable(typeof(SaferPicture))]
    [JsonSerializable(typeof(Scene))]
    [JsonSerializable(typeof(SceneVerdict))]
    [JsonSerializable(typeof(FootageSeen))]
    [JsonSerializable(typeof(PublishNotes))]
    private sealed partial class ReplyJson : JsonSerializerContext;

    private sealed record TierDto(string Dialogue, string VisualPrompt, string Pose);
    // Footage is the number of the owner's file the clip shows, from 1; 0 for none.
    private sealed record ClipDto(string Title, TierDto B, string? Kind = null, string Beat = "", int Footage = 0, bool Host = false);
    private sealed record DriftDto(bool CoreChanged);
    private sealed record RewriteDto(string Dialogue);
    private sealed record ReviewDto(List<ClipNote> Notes);
    private sealed record MomentDto(int Clip, double Start, double Length);
    private sealed record TeaserDto(string Hook, List<MomentDto> Moments);
    private sealed record TeasersDto(List<TeaserDto> Teasers);
    private sealed record PlannedClipDto(string Title, string Kind, string About, string Beat = "", int Footage = 0, bool Host = false);
    private sealed record OutlineDto(string Title, List<PlannedClipDto> Clips, string Subject = "");
    private sealed record SearchDto(List<string> Terms);
    private sealed record HostDto(bool Same, string Difference);
    private sealed record LetteringDto(bool Lettered, string Text);
    private sealed record ShotDto(string Beat, string Shot);
    private sealed record ShotsDto(List<ShotDto> Shots);

    /// <summary>How many clips are written at once. More than this and the service starts turning requests away as too many.</summary>
    private const int ClipsAtOnce = 4;

    /// <summary>Told, in words fit to show, of anything out of the ordinary in how a script came to be written.</summary>
    public Action<string>? Note { get; set; }

    /// <summary>Told what each request used: its name in the spend log, how many tokens, and their price when the rates are known.</summary>
    public Action<string, int, decimal>? Used { get; set; }

    public Show Show { get; set; }

    /// <summary>Which picture types can be made on this computer; the model is offered only those.</summary>
    public Func<VisualKind, bool> CanMake { get; set; } = _ => true;

    /// <summary>The fast model with pictures to look at; null when nothing here can see them, and looking is then skipped.</summary>
    public SeeingChat? See { get; init; }

    /// <summary>
    /// A model on this computer (<see cref="AppSettings.LocalModel"/>), for the small jobs that need no great skill. Free;
    /// when it is not running or answers badly, the fast model does the job instead.
    /// </summary>
    public JsonChat? Local { get; init; }

    public async Task<Episode> WriteEpisodeAsync(Lesson lesson, IReadOnlyList<GroundingSnippet> grounding, EpisodeLength length, CancellationToken ct, IProgress<IReadOnlyList<string>>? clipsWritten = null)
    {
        var beats = BeatPlan.Beats(lesson.Structure);
        var show = BeatPlan.ShowOf(lesson.Structure);
        var request = Request(lesson, grounding, length, beats);
        IReadOnlyList<KindInfo> kinds = [.. Visuals.Kinds.Where(k => CanMake(k.Kind))];
        var (system, schema) = (ScriptSchemas.OutlineSystem(kinds, show), ScriptSchemas.Outline(kinds, beats));

        // A plan is short, so it comes back quickly. Too many clips are simply trimmed below. Too few is topped up
        // once, asking only for the clips that are missing so the ones already planned are kept.
        var outline = await Ask<OutlineDto>(settings.ChatDeployment, system, request, "outline", schema, ct);
        if (outline.Clips.Count < length.MinClips)
        {
            var missing = length.MinClips - outline.Clips.Count;
            var again = request + Environment.NewLine +
                $"Your last plan had only {outline.Clips.Count} clips: {string.Join("; ", outline.Clips.Select(c => c.Title))}. " +
                $"Plan {missing} more on subtopics those do not cover, and return only the new clips." + Environment.NewLine;
            var more = await Ask<OutlineDto>(settings.ChatDeployment, system, again, "outline", schema, ct);
            outline = outline with { Clips = [.. outline.Clips, .. more.Clips] };
        }
        if (outline.Clips.Count < length.MinClips)
            throw new InvalidDataException($"The script came back with only {outline.Clips.Count} clips. Please try again.");

        var subject = outline.Subject?.Trim() is { Length: > 0 } said ? said : null;
        var (title, written) = await WriteClipsAsync(request, outline, kinds, lesson.Footage, length, show, clipsWritten, ct);
        var footage = lesson.Footage;
        var clips = written.Select(c =>
        {
            var clip = new Clip(
                Guid.NewGuid(), c.Title, Tier.B,
                new Dictionary<Tier, TierScript> { [Tier.B] = new(c.B.Dialogue, c.B.VisualPrompt, c.B.Pose) },
                new VisualSpec(Visuals.KindFor(c.Kind)), HostVisible: c.Host && show == Show.Robot) // the kids' series has no presenter on screen
            { Beat = c.Beat };
            return c.Footage >= 1 && c.Footage <= footage.Count ? BeatPlan.WithFootage(clip, footage[c.Footage - 1]) : clip;
        }).ToList();
        // The model's beats and footage numbers are not trusted further than this: the plan is held to its structure.
        var episode = new Episode(title, lesson.Topic, clips, MixSeed: Random.Shared.Next()) { Structure = lesson.Structure, Subject = subject };
        return BeatPlan.Repair(episode, footage, length.MaxClips);
    }

    /// <summary>
    /// What every request for this script starts with: the number of clips, the beats, the owner's footage, the
    /// lesson, the series so far and the references. The same text opens every request, so the service has it
    /// cached after the first and charges less for it.
    /// </summary>
    private static string Request(Lesson lesson, IReadOnlyList<GroundingSnippet> grounding, EpisodeLength length, IReadOnlyList<Beat> beats)
    {
        var prompt = new StringBuilder()
            .AppendLine($"Number of clips: {length.InWords}, besides any more the footage needs.")
            .AppendLine($"Beats, in order: {string.Join(", ", beats.Select(b => b.Name))}. Real footage belongs in {string.Join(", ", beats.Where(b => b.ForFootage).Select(b => b.Name))}.")
            .AppendLine(lesson.Footage.Count == 0 ? "Footage: none. Every clip has footage 0." : "Footage:");
        // The owner's own notes, so not fenced: they say where each of their clips belongs. What a model saw in a
        // film, and what is said in it, did not come from the owner, so those are.
        foreach (var (note, i) in lesson.Footage.Select((n, i) => (n, i)))
            prompt.AppendLine(FormattableString.Invariant($"{i + 1}. {note.Length.TotalSeconds:0} seconds, {(note.OwnSound ? "keeps its own sound" : $"narrated over in about {Durations.WordsFor(note.Length)} words")}: {note.Note.ReplaceLineEndings(" ")}"));
        foreach (var (note, i) in lesson.Footage.Select((n, i) => (n, i)))
        {
            if (note.Seen is { } seen)
                prompt.Append(Fenced("existing", $" what=\"what a model saw happen in footage {i + 1}\"", seen.Timeline));
            if (note.Heard is { Count: > 0 } heard)
                prompt.Append(Fenced("existing", $" what=\"what is said in footage {i + 1}\"", string.Join(' ', heard.Select(w => w.Text))));
        }
        if (lesson.Earlier.Count > 0)
            prompt.AppendLine().Append(Fenced("existing", " what=\"earlier episodes of the series\"", string.Join('\n', lesson.Earlier)));
        prompt.AppendLine().Append(Fenced("topic", "", Capped(lesson.Topic)));
        foreach (var snippet in grounding)
            prompt.AppendLine().Append(Fenced("reference", $" source=\"{Attribute(snippet.Repo)} · {Attribute(snippet.Path)}\" url=\"{Attribute(snippet.Url)}\"", snippet.Text));
        return prompt.ToString();
    }

    /// <summary>What a clip the model would not write says until the user writes it.</summary>
    public const string LeftToWrite = "Write what the narrator says here. The script service would not write this clip.";

    /// <summary>
    /// Writes every clip of a plan, a few at a time. A clip that is turned away is left for the user to write, under
    /// the title and picture the plan gave it, and the rest are written as usual. A film that keeps its own sound has
    /// nothing narrated over it, so nothing is asked for it.
    /// </summary>
    /// <param name="request">The request the plan was made from: the number of clips, the lesson and the references.</param>
    private async Task<(string Title, IReadOnlyList<ClipDto> Clips)> WriteClipsAsync(
        string request, OutlineDto outline, IReadOnlyList<KindInfo> kinds, IReadOnlyList<FootageNote> footage, EpisodeLength length, Show show, IProgress<IReadOnlyList<string>>? clipsWritten, CancellationToken ct)
    {
        // Each clip is a request of its own, so only as many are written as the length allows. Clips that show the
        // owner's footage are always written; the others fill the room left.
        var room = length.MaxClips - outline.Clips.Count(c => c.Footage > 0);
        var planned = new List<PlannedClipDto>();
        foreach (var clip in outline.Clips)
            if (clip.Footage > 0 || room-- > 0) planned.Add(clip);
        var plan = string.Join('\n', planned.Select((c, i) => $"{i + 1}. {c.Beat}: {c.Title}: {c.About}"));
        var clips = new ClipDto?[planned.Count];
        var left = new bool[planned.Count];
        var asked = 0;

        async Task WriteAsync(int i)
        {
            var clip = planned[i];
            var film = clip.Footage >= 1 && clip.Footage <= footage.Count ? footage[clip.Footage - 1] : null;
            if (film is { OwnSound: true })
            {
                clips[i] = new ClipDto(clip.Title, new TierDto("", clip.About, "none"), clip.Kind, clip.Beat, clip.Footage, clip.Host);
            }
            else
            {
                var mine = new StringBuilder(request).AppendLine()
                    .Append(Fenced("existing", " what=\"the plan of the whole video\"", $"{outline.Title}\n{plan}"))
                    .AppendLine($"Write clip {i + 1} of {planned.Count}.{(film is null ? "" : $" It shows footage {clip.Footage}, in about {Durations.WordsFor(film.Length)} words.")}")
                    .AppendLine(kinds.FirstOrDefault(k => k.Key == clip.Kind) is { } kind ? $"Picture type: {kind.Key}. {kind.Brief}" : "Picture type: title. visualPrompt: the title.");
                Interlocked.Increment(ref asked);
                try
                {
                    clips[i] = new ClipDto(clip.Title, await Ask<TierDto>(settings.ChatDeployment, ScriptSchemas.ClipSystem(show), mine.ToString(), "tier", ScriptSchemas.Tier, ct), clip.Kind, clip.Beat, clip.Footage, clip.Host);
                }
                catch (ScriptDeclinedException)
                {
                    // Kept as a title card: there is nothing yet to draw a picture of.
                    clips[i] = new ClipDto(clip.Title, new TierDto(LeftToWrite, clip.About, "none"), "title", clip.Beat, clip.Footage, clip.Host);
                    left[i] = true;
                }
            }
            lock (clips)
                clipsWritten?.Report([.. clips.OfType<ClipDto>().Select(c => c.Title)]);
        }

        // The first clip is written alone: the others then find the long opening they share already cached, which
        // costs less and answers sooner. Then the rest, a few at a time.
        if (planned.Count > 0)
            await WriteAsync(0);
        await Batch.EachAsync(Enumerable.Range(1, Math.Max(0, planned.Count - 1)), ClipsAtOnce, WriteAsync, ct);

        var unwritten = planned.Where((_, i) => left[i]).Select(c => c.Title).ToList();
        if (unwritten.Count > 0 && unwritten.Count == asked)
            throw new ScriptDeclinedException(DeclinedBy.Model, null, settings.ChatDeployment); // nothing was written: there is no episode to hand back
        if (unwritten.Count > 0)
            Note?.Invoke($"{(unwritten.Count == 1 ? "1 clip was" : $"{unwritten.Count} clips were")} left for you to write, as the script service would not: {string.Join(", ", unwritten)}.");
        return (outline.Title, [.. clips.OfType<ClipDto>()]);
    }

    private static string Capped(string topic) => topic.Length > IScriptWriter.MaxTopicLength ? topic[..IScriptWriter.MaxTopicLength] : topic;

    /// <summary>
    /// Text from a repository, a model or a paste goes inside a tag the system prompts tell the model not to obey.
    /// Any copy of the closing tag, however it is spaced or cased, is removed first, so the text cannot end its own fence
    /// early. Removed until none is left: taking one out of "&lt;&lt;/x&gt;/x&gt;" would otherwise join the rest into another.
    /// </summary>
    public static string Fenced(string tag, string attributes, string text)
    {
        for (var was = ""; was != text;)
            (was, text) = (text, Regex.Replace(text, $@"<\s*/\s*{tag}\s*>", "", RegexOptions.IgnoreCase));
        return $"<{tag}{attributes}>\n{text}\n</{tag}>\n";
    }

    // A repository path or link sits inside quotes on the opening tag; it must not be able to close them.
    private static string Attribute(string value) => value.Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

    private Task<T> Ask<T>(string deployment, string system, string user, string schemaName, string schema, CancellationToken ct) =>
        AskWith<T>(chat, deployment, system, user, schemaName, schema, ct);

    /// <summary>A small job: the model on this computer when there is one, else the fast model.</summary>
    private async Task<T> AskLight<T>(string system, string user, string schemaName, string schema, CancellationToken ct)
    {
        if (Local is { } local && settings.LocalModel is { } model)
            try
            {
                return await AskWith<T>(local, model, system, user, schemaName, schema, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is not ScriptDeclinedException)
            {
                // Not running, too slow, or its answer did not read: the fast model is asked instead. Nothing is lost
                // but a moment, so nothing is said.
            }
        return await Ask<T>(settings.FastChatDeployment, system, user, schemaName, schema, ct);
    }

    private static async Task<T> AskWith<T>(JsonChat via, string deployment, string system, string user, string schemaName, string schema, CancellationToken ct)
    {
        var reply = await via(deployment, system, user, schemaName, schema, null, ct);
        var value = Read<T>(reply);
        // Some models answer in exactly the shape asked, with "I cannot assist with that" where the script should
        // be. Taken at its word that would be saved, voiced and drawn as an episode.
        string[] words = value switch
        {
            OutlineDto plan => [plan.Title, .. plan.Clips.Select(c => c.About)],
            TierDto tier => [tier.Dialogue],
            RewriteDto rewrite => [rewrite.Dialogue],
            _ => [],
        };
        if (words.Any(ScriptDeclinedException.IsRefusal))
            throw new ScriptDeclinedException(DeclinedBy.Model, null, deployment);
        return value;
    }

    private static T Read<T>(string reply)
    {
        try
        {
            var value = Json.Read<T>(reply, Replies);
            // The serializer checks every field, but not for a null in the middle of a list.
            if (value is null || (value is OutlineDto outline && outline.Clips.Contains(null!)))
                throw new JsonException("Reply is missing required fields.");
            return value;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The model's reply could not be read. Please try again.", e);
        }
    }
}
