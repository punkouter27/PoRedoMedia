using System.Text.RegularExpressions;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Comics;

/// <summary>One person in the strip: how they look in every panel and which voice speaks for them.</summary>
public sealed record ComicCharacter(string Name, string Look, string Voice);

/// <summary>One spoken line. <see cref="Delivery"/> tells the voice how to say it.</summary>
public sealed record ComicLine(string Speaker, string Text, string Delivery);

/// <summary>
/// One panel: what the picture shows (no words in it), the narrator's caption box, the speech bubbles, a meme sound for
/// the moment, whether a studio audience laughs and claps after it (at the right moments only), and the offscreen
/// heckler's wisecrack after the lines. Empty strings mean "none".
/// </summary>
public sealed record ComicPanel(string Scene, string Caption, IReadOnlyList<ComicLine> Lines, string Sound, bool Laugh = false, string Heckle = "", bool Applause = false);

/// <summary>The whole strip, ready to draw and voice. Always holds <see cref="MinPanels"/> to <see cref="MaxPanels"/> panels.</summary>
public sealed record ComicScript(string Title, string ArtStyle, string Music, IReadOnlyList<ComicCharacter> Characters, IReadOnlyList<ComicPanel> Panels)
{
    public const int MinPanels = ComicRequest.MinPanels, MaxPanels = ComicRequest.MaxPanels, MaxLinesPerPanel = 2, MaxLineLength = 160, MaxCaptionLength = 140, MaxHeckleLength = 90;


    public const string DefaultStyle = "Bold, clean American newspaper comic strip: thick black ink outlines, flat bright colours, expressive cartoon faces";

    /// <summary>The voice for a speaker; an unknown name gets the narrator's.</summary>
    public string VoiceOf(string speaker, ComicOptions options) =>
        Characters.FirstOrDefault(c => c.Name.Equals(speaker, StringComparison.OrdinalIgnoreCase))?.Voice ?? options.NarratorVoice.Voice;

    /// <summary>The comic as a transcript, kept beside it in the Library.</summary>
    public string Transcript() => string.Join("\n\n", Panels.Select((p, i) =>
        string.Join("\n", new[] { $"Panel {i + 1}" }
            .Concat(p.Caption.Length > 0 ? [$"[{p.Caption}]"] : [])
            .Concat(p.Lines.Select(l => $"{l.Speaker}: {l.Text}"))
            .Concat(p.Heckle.Length > 0 ? [$"Heckler: {p.Heckle}"] : []))));

    /// <summary>
    /// Turns whatever the writer sent into a script that can be drawn: blanks dropped, words cut to fit a bubble,
    /// panels kept to 3–8 (a short draft is padded from the conversation's own lines), the sounds, laughs and heckles
    /// held to what <paramref name="options"/> allows, and each speaker given a voice of their own, never the narrator's
    /// or the heckler's.
    /// </summary>
    public static ComicScript Normalize(ComicDraft draft, string conversation, ComicOptions? options = null)
    {
        options ??= ComicOptions.Default;
        var panels = ToPanels(draft.Panels).ToList();
        if (panels.Count < MinPanels)
            panels.AddRange(ToPanels(Fallback(conversation).Panels).Skip(panels.Count).Take(MinPanels - panels.Count));
        // A laugh only lands after something is said, and only a few times a comic, or it stops being funny; the same
        // goes for meme sounds and heckles, each held to its slider.
        var (laughs, sounds, heckles, claps) = (0, 0, 0, 0);
        var maxHeckles = options.MaxHeckles(panels.Count);
        for (var i = 0; i < panels.Count; i++)
        {
            var p = panels[i];
            if (p.Heckle.Length > 0 && ++heckles > maxHeckles)
                p = p with { Heckle = "" };
            if (p.Laugh && ((p.Lines.Count == 0 && p.Heckle.Length == 0) || ++laughs > options.MaxLaughs))
                p = p with { Laugh = false };
            if (p.Applause && ++claps > options.MaxApplause)
                p = p with { Applause = false };
            if (p.Sound.Length > 0 && ++sounds > options.MaxSounds)
                p = p with { Sound = "" };
            panels[i] = p;
        }
        while (panels.Count < MinPanels)
            panels.Add(new ComicPanel("The characters stare at their phones in stunned silence.", panels.Count == MinPanels - 1 ? "And that was that." : "Meanwhile...", [], ""));

        var speakers = panels.SelectMany(p => p.Lines).Select(l => l.Speaker).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var drafted = (draft.Characters ?? []).Where(c => !string.IsNullOrWhiteSpace(c.Name)).ToList();
        var voices = AiFoundrySpeech.Voices.Where(v => !options.Reserved.Contains(v)).ToList();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var characters = new List<ComicCharacter>();
        foreach (var name in speakers)
        {
            var known = drafted.FirstOrDefault(c => Name(c.Name).Equals(name, StringComparison.OrdinalIgnoreCase));
            var voice = known?.Voice?.Trim().ToLowerInvariant() is { } asked && voices.Contains(asked) && !used.Contains(asked)
                ? asked
                : voices.FirstOrDefault(v => !used.Contains(v)) ?? voices[characters.Count % voices.Count];
            used.Add(voice);
            characters.Add(new ComicCharacter(name, Clean(known?.Look, 200) is { Length: > 0 } look ? look : $"a cartoon person called {name}", voice));
        }

        return new ComicScript(
            Clean(draft.Title, 60) is { Length: > 0 } title ? title : "A Conversation",
            Clean(draft.ArtStyle, 200) is { Length: > 0 } style ? style : DefaultStyle,
            Clean(draft.Music, 40),
            characters,
            panels);
    }

    /// <summary>
    /// The stand-in when no chat model can write the script: the conversation's own lines, as written, two or three to
    /// a panel. A line reads as "Name: words"; one without a name continues whoever spoke last.
    /// </summary>
    public static ComicDraft Fallback(string conversation)
    {
        // A camera log has no "Name: words" rows of its own; what it quotes people saying is the conversation.
        if (ObservationLog.IsLog(conversation) && ObservationLog.Speech(conversation) is { Count: > 0 } speech)
            conversation = string.Join("\n", speech);
        var lines = ParseLines(conversation);
        if (lines.Count == 0)
            lines = [new ComicLineDraft("Someone", Clean(conversation, MaxLineLength), "")];
        var perPanel = Math.Clamp((int)Math.Ceiling(lines.Count / (double)MaxPanels), 1, MaxLinesPerPanel);
        var chunks = lines.Chunk(perPanel).Take(MaxPanels).ToList();
        var panels = chunks.Select((chunk, i) => new ComicPanelDraft(
            $"{string.Join(" and ", chunk.Select(l => l.Speaker).Distinct())} talking, reacting with big cartoon expressions.",
            i == 0 ? "It all started with a message..." : "",
            chunk,
            i == chunks.Count - 1 ? "dramatic" : "",
            i == chunks.Count - 1, // the last exchange gets the laugh and the applause
            null,
            i == chunks.Count - 1)).ToList();
        var names = lines.Select(l => l.Speaker!).Distinct(StringComparer.OrdinalIgnoreCase);
        return new ComicDraft("A Conversation", DefaultStyle, "", [.. names.Select(n => new ComicCharacterDraft(n, null, null))], [.. panels]);
    }

    // "Name: words", "[10:42] Name: words", "Name (10:42): words". A name is at most four words and starts with a letter.
    private static readonly Regex Speaker = new(@"^\s*(?:\[[^\]]{1,30}\]\s*)?(?<name>[\p{L}][\p{L}\p{N}.' _-]{0,30}?)\s*(?:\([^)]{1,20}\))?\s*:\s+(?<text>.+)$", RegexOptions.Compiled);

    // An email's "From: Maya Lopez <maya@x.com>" (or a bare address), the headers that go with it, and the noise a copied
    // thread carries: "On Mon, ... wrote:", "Sent from my iPhone", "Delivered", "Read 10:42".
    private static readonly Regex From = new(@"^From:\s*(?:""?(?<name>[^""<]+?)""?\s*<[^>]*>|(?<address>[^@\s]+)@\S+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Noise = new(@"^(?:(?:To|Cc|Bcc|Subject|Date|Sent):.*|On .{4,80} wrote:|Sent from my .*|Delivered|Read(?: \d{1,2}:\d{2}.*)?|-{2,}.*Original Message.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The conversation's messages in the order they were sent. Chat lines read as "Name: words"; an email chain is read by
    /// its From headers, and, since a chain lists the newest message first, put back in order.
    /// </summary>
    internal static List<ComicLineDraft> ParseLines(string conversation)
    {
        var lines = new List<ComicLineDraft>();
        string? sender = null; // the email being read, until the next From
        var newMessage = false;
        var emails = 0;
        var messageOf = new List<int>(); // which email each line belongs to
        foreach (var raw in conversation.Split('\n'))
        {
            var row = raw.Trim();
            if (row.Length == 0 || row.StartsWith('>') || Noise.IsMatch(row))
                continue; // quoted earlier mail, headers, phone noise
            if (From.Match(row) is { Success: true } from)
            {
                sender = from.Groups["name"].Success ? from.Groups["name"].Value.Trim() : from.Groups["address"].Value;
                newMessage = true;
                emails++;
                continue;
            }
            if (sender is null && Speaker.Match(row) is { Success: true } m && m.Groups["name"].Value.Split(' ').Length <= 4)
                lines.Add(new ComicLineDraft(m.Groups["name"].Value.Trim(), Clean(m.Groups["text"].Value, MaxLineLength), ""));
            else if (!newMessage && lines.Count > 0 && lines[^1].Text!.Length + row.Length < MaxLineLength)
                lines[^1] = lines[^1] with { Text = $"{lines[^1].Text} {row}" };
            else
                lines.Add(new ComicLineDraft(sender ?? (lines.Count > 0 ? lines[^1].Speaker : "Someone"), Clean(row, MaxLineLength), ""));
            while (messageOf.Count < lines.Count)
                messageOf.Add(emails);
            newMessage = false;
        }
        // Whole emails go back in order; the lines of one long email keep theirs.
        return emails > 1
            ? [.. lines.Select((line, i) => (line, i)).OrderByDescending(x => messageOf[x.i]).ThenBy(x => x.i).Select(x => x.line)]
            : lines;
    }

    /// <summary>Speakers a writer sometimes uses for narration, which belongs in the caption box, not a bubble.</summary>
    private static bool IsNarrator(string? speaker) =>
        speaker?.Trim().ToLowerInvariant() is "narrator" or "narration" or "caption" or "the narrator";

    private static IEnumerable<ComicPanel> ToPanels(ComicPanelDraft[]? drafts) => (drafts ?? [])
        .Select(p =>
        {
            var said = (p.Lines ?? []).Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
            var caption = string.Join(" ", new[] { p.Caption }.Concat(said.Where(l => IsNarrator(l.Speaker)).Select(l => l.Text)).Where(t => !string.IsNullOrWhiteSpace(t)));
            return new ComicPanel(
                Clean(p.Scene, 400),
                Clean(caption, MaxCaptionLength),
                [.. said
                    .Where(l => !IsNarrator(l.Speaker))
                    .Take(MaxLinesPerPanel)
                    .Select(l => new ComicLine(Name(l.Speaker) is { Length: > 0 } who ? who : "Someone", Clean(l.Text, MaxLineLength), Clean(l.Delivery, 80)))],
                Clean(p.Sound, 40),
                p.Laugh == true,
                Clean(p.Heckle, MaxHeckleLength),
                p.Applause == true);
        })
        .Where(p => p.Scene.Length > 0 || p.Lines.Count > 0 || p.Caption.Length > 0)
        .Take(MaxPanels);

    /// <summary>A speaker's name as a bubble shows it: no description in brackets ("Partner (car driver)" is "Partner").</summary>
    internal static string Name(string? speaker) => Clean(Regex.Replace(speaker ?? "", @"\s*[\(\[][^\)\]]*[\)\]]", ""), 40);

    private static string Clean(string? text, int most)
    {
        var flat = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return flat.Length <= most ? flat : flat[..(most - 1)].TrimEnd() + "…";
    }
}

// What the chat model is asked to fill in. Every field may come back missing; Normalize copes.
public sealed record ComicDraft(string? Title, string? ArtStyle, string? Music, ComicCharacterDraft[]? Characters, ComicPanelDraft[]? Panels);

public sealed record ComicCharacterDraft(string? Name, string? Look, string? Voice);

public sealed record ComicPanelDraft(string? Scene, string? Caption, ComicLineDraft[]? Lines, string? Sound, bool? Laugh = null, string? Heckle = null, bool? Applause = null);

public sealed record ComicLineDraft(string? Speaker, string? Text, string? Delivery);
