using System.Text;
using System.Text.RegularExpressions;

namespace PoRedoMedia.Api.Features.Comics;

/// <summary>
/// A room camera's written log (PoWatch's journal, for one) pasted in place of a conversation: "At 16:53, Person 26 and
/// several people came in. The camera saw this: “…”. Person 26 said: “…”." Machine-written, so most of each paragraph is
/// the same weather report (light, motion, loudness), and the people are numbered rather than named. This class tells
/// such a log from a chat, cuts it to the parts a story can use, and pulls out what was actually said.
/// </summary>
internal static partial class ObservationLog
{
    /// <summary>True when the paste reads as a timestamped camera log rather than a chat or an email.</summary>
    public static bool IsLog(string text)
    {
        var paragraphs = Paragraph().Count(text);
        // Two "At 16:53," paragraphs, or one with the log's own phrases: a chat never reads like this.
        return paragraphs >= 2 || (paragraphs == 1 && LogPhrase().IsMatch(text));
    }

    /// <summary>
    /// The log with its filler removed: light, stillness, where motion was, loudness, crowd size and who stayed longest
    /// go; arrivals, departures, speech, sounds, objects and the camera's own captions stay. A run of paragraphs where
    /// nobody came, spoke or was seen becomes one line saying how long the room sat empty.
    /// </summary>
    public static string Distill(string text)
    {
        var output = new StringBuilder();
        string? emptySince = null; // the time an empty stretch began, until something happens again

        foreach (var block in Blocks(text))
        {
            var kept = Filler().Replace(block, "");
            kept = Spaces().Replace(kept, " ").Trim();
            var time = Paragraph().Match(kept) is { Success: true } at ? at.Groups["time"].Value : null;

            if (!HasEvent().IsMatch(kept))
            {
                emptySince ??= time ?? "a while";
                continue;
            }
            if (emptySince is not null)
            {
                output.Append(EmptyStretch(emptySince, time)).Append("\n\n");
                emptySince = null;
            }
            output.Append(kept).Append("\n\n");
        }
        if (emptySince is not null)
            output.Append(EmptyStretch(emptySince, null)).Append("\n\n");
        return output.ToString().Trim();
    }

    /// <summary>What was said, as "Speaker: words" lines in order ("Someone" when the log names nobody).</summary>
    public static IReadOnlyList<string> Speech(string text) =>
        [.. Said().Matches(text).Select(m => $"{(m.Groups["who"].Value is "Someone" or "" ? "Someone" : m.Groups["who"].Value)}: {m.Groups["words"].Value.Trim()}")];

    private static string EmptyStretch(string from, string? to) =>
        to is null ? $"(From {from} on, the room sat empty.)" : $"(From {from} to {to}, the room sat empty.)";

    /// <summary>The paste split into paragraphs; a log pasted without blank lines splits at each "At 16:53,".</summary>
    private static IEnumerable<string> Blocks(string text) =>
        BlockStart().Split(text.Replace("\r\n", "\n")).Select(b => b.Trim()).Where(b => b.Length > 0);

    [GeneratedRegex(@"(?m)^\s*At (?<time>\d{1,2}:\d{2}),")]
    private static partial Regex Paragraph();

    [GeneratedRegex(@"(?m)\n\s*\n|(?=^\s*At \d{1,2}:\d{2},)")]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"The camera saw this:|were in view|came in from|left again|The microphone picked up")]
    private static partial Regex LogPhrase();

    // Something a story can use happened: someone came, went or spoke, the camera described the scene, or a sound.
    [GeneratedRegex(@"came in|left again|said:|The camera saw this:|The microphone picked up")]
    private static partial Regex HasEvent();

    // The sentences every paragraph repeats. None of them holds a quotation, so [^.“"]* cannot eat into speech.
    [GeneratedRegex(
        @"The room stayed [^.“""]*\.|The room (?:got|grew|went) [^.“""]*\.|Between \d{1,2}:\d{2} and \d{1,2}:\d{2} nothing moved at all\."
        + @"|Most of the movement was [^.“""]*\.|There was (?:a |an )?[^.“""]*(?:murmur|hum|noise)\.|It was quiet\."
        + @"|The (?:loudest|liveliest|busiest) moment (?:was|came) at \d{1,2}:\d{2}\.|At the busiest, [^.“""]*\."
        + @"|[^.“""]* stayed longest, about [^.“""]*\.")]
    private static partial Regex Filler();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();

    // "Person 26 said: “I am Mommy from here.”" and "Someone said: "Yay!"", curly or straight quotes.
    [GeneratedRegex(@"(?<who>Someone|Person \d+|[A-Z][\w'-]*(?: [A-Z][\w'-]*)?) said: [“""](?<words>[^”""]+)[”""]")]
    private static partial Regex Said();
}
