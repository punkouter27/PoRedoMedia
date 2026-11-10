namespace PoRedoMedia.Movie.Library;

/// <summary>What the owner taught the robot in one episode. The script is written around it.</summary>
/// <param name="Taught">What was taught, in the owner's words.</param>
/// <param name="WentWrong">What went wrong on the way; empty when nothing is said.</param>
/// <param name="Footage">The owner's own clips of the robot, each placed in one clip of the episode.</param>
public sealed record Lesson(string Taught, string WentWrong, BeatStructure Structure, IReadOnlyList<FootageNote> Footage)
{
    /// <summary>
    /// The lesson as one piece of text: what is saved as the episode's topic and what the model reads. In the kids'
    /// series the second box holds the funny moments, not what went wrong.
    /// </summary>
    public string Topic => WentWrong.Trim().Length > 0
        ? $"{Taught.Trim()}\n\n{SecondBox(BeatPlan.ShowOf(Structure))}: {WentWrong.Trim()}"
        : Taught.Trim();

    /// <summary>What the second box holds in each series: what went wrong, the funny moments, or the moments not to miss.</summary>
    public static string SecondBox(Show show) => show switch { Show.Kids => "Funny moments", Show.General => "Moments not to miss", _ => "What went wrong" };

    /// <summary>The series so far, a line to each episode ("#3 · Standing up: …"), so this one can build on them.</summary>
    public IReadOnlyList<string> Earlier { get; init; } = [];
}

/// <summary>One shot to film for a beat, from the list asked for before filming; ticked once it is filmed.</summary>
public sealed record Shot(string Beat, string What, bool Done = false);

/// <param name="Path">The file as the owner picked it. It is copied into the episode once the script is written.</param>
/// <param name="Note">A line on what it shows, which tells the script where it belongs.</param>
/// <param name="OwnSound">It plays with its own sound; otherwise it is narrated over.</param>
public sealed record FootageNote(string Path, string Note, bool OwnSound, TimeSpan Length)
{
    /// <summary>What a model saw happen in the film; null when it was not watched.</summary>
    public FootageSeen? Seen { get; init; }

    /// <summary>What is said in the film, with when each word is said; null when it was not listened to.</summary>
    public IReadOnlyList<WordTiming>? Heard { get; init; }
}
