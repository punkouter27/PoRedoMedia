using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using PoRedoMedia.Api.Features.Comics;

namespace PoRedoMedia.UnitTests;

/// <summary>A room camera's log pasted into Comic-ify: told from a chat, cut to its story, and planned before it is written.</summary>
public sealed class ObservationLogTests
{
    // Part of a real PoWatch journal: the owner's own sample of "random source material".
    private const string Log =
        "At 16:03, several people came in from the left. The camera saw this: “A man in a yellow shirt is looking at the camera.” "
        + "Someone said: “Okay, what's going on? Okay, he's taking a nap.” The microphone picked up throat clearing. A laptop, a "
        + "toilet and a remote were in view. Several people left again. The room stayed softly lit throughout. Between 16:03 and "
        + "16:13 nothing moved at all. Most of the movement was near the camera. There was a soft background murmur. The loudest "
        + "moment was at 16:05. At the busiest, five were in view together.\n\n"
        + "At 16:53, Person 27, Person 5 and Person 26 came in from the near side. The camera saw this: “A man in a yellow shirt "
        + "looking into a mirror.” Person 26 said: “I am Mommy from here.” A sink and a microwave were in view. Person 26 left "
        + "again by the near side. The room stayed softly lit throughout. The liveliest moment came at 16:58. It was quiet. "
        + "Person 2 stayed longest, about 6 minutes.\n\n"
        + "At 17:33, a laptop and a tv were in view. The room stayed softly lit throughout. Between 17:33 and 17:43 nothing "
        + "moved at all. There was a soft background murmur.\n\n"
        + "At 17:43, a tv and a laptop were in view. The room stayed softly lit throughout. Between 17:43 and 17:53 nothing "
        + "moved at all. There was a soft background murmur.\n\n"
        + "At 17:53, several people came in. Someone said: “Your answer is a pizza.” The microphone picked up a door slamming.";

    [Fact]
    public void A_camera_log_is_told_from_a_chat_and_an_email()
    {
        Assert.True(ObservationLog.IsLog(Log));
        Assert.False(ObservationLog.IsLog("[10:42] Maya: did you take my leftovers\nSam: define \"take\""));
        Assert.False(ObservationLog.IsLog("From: Sam <sam@example.com>\nSubject: At 10:00, lunch?\nSure."));
    }

    [Fact]
    public void The_log_keeps_what_happened_and_said_drops_the_weather_report_and_folds_empty_stretches()
    {
        var story = ObservationLog.Distill(Log);

        Assert.Contains("Someone said: “Okay, what's going on? Okay, he's taking a nap.”", story, StringComparison.Ordinal);
        Assert.Contains("Person 26 said: “I am Mommy from here.”", story, StringComparison.Ordinal);
        Assert.Contains("A man in a yellow shirt looking into a mirror.", story, StringComparison.Ordinal);
        Assert.Contains("came in from the near side", story, StringComparison.Ordinal);
        foreach (var filler in new[] { "softly lit", "nothing moved", "Most of the movement", "murmur", "loudest", "liveliest", "At the busiest", "stayed longest", "It was quiet" })
            Assert.DoesNotContain(filler, story, StringComparison.Ordinal);
        // The two paragraphs where nobody came or spoke become one time skip, up to the next thing that happened.
        Assert.Contains("(From 17:33 to 17:53, the room sat empty.)", story, StringComparison.Ordinal);
        // This sample is mostly speech; a whole journal, mostly filler, shrinks far more.
        Assert.True(story.Length < Log.Length * 0.6, $"{story.Length} of {Log.Length} characters kept");
    }

    [Fact]
    public void With_no_chat_model_the_log_is_drawn_from_what_people_said()
    {
        var script = ComicScript.Normalize(ComicScript.Fallback(Log), Log);

        var lines = script.Panels.SelectMany(p => p.Lines).ToList();
        Assert.Equal(["Someone", "Person 26", "Someone"], lines.Select(l => l.Speaker));
        Assert.Equal("Your answer is a pizza.", lines[^1].Text);
        Assert.DoesNotContain(lines, l => l.Text.Contains("softly lit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_log_is_planned_first_and_the_comic_is_written_from_the_plan_and_the_cut_log()
    {
        var chat = new RecordingChat(
            """{"premise":"Yellow Shirt wants dinner and nobody will decide.","cast":[{"name":"Yellow Shirt","look":"a tall man in a yellow shirt","standsFor":"the man in yellow"}],"beats":[{"happens":"He naps through the planning","uses":"he's taking a nap"},{"happens":"The answer arrives","uses":"Your answer is a pizza."}]}""",
            """{"title":"The Pizza Verdict","panels":[{"scene":"A cluttered office","caption":"Dinner, undecided.","lines":[{"speaker":"Yellow Shirt","text":"Your answer is a pizza.","delivery":"smug"}],"sound":""}]}""");

        var script = await new ComicWriter(NullLogger<ComicWriter>.Instance, chat).WriteAsync(Log, ComicOptions.Default, CancellationToken.None);

        Assert.Equal("The Pizza Verdict", script.Title);
        Assert.Equal(2, chat.Calls.Count);
        var (planSystem, planUser) = chat.Calls[0];
        Assert.Contains("camera's automatic log", planSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("softly lit", planUser, StringComparison.Ordinal); // the planner sees the cut log too
        var (comicSystem, comicUser) = chat.Calls[1];
        Assert.Contains("STEP 1, cast it.", comicSystem, StringComparison.Ordinal);
        Assert.StartsWith("The story plan:\nPremise: Yellow Shirt wants dinner", comicUser, StringComparison.Ordinal);
        Assert.Contains("Beat 1: He naps through the planning [uses: he's taking a nap]", comicUser, StringComparison.Ordinal);
        Assert.Contains("The camera log:\n<<<", comicUser, StringComparison.Ordinal);
        Assert.DoesNotContain("softly lit", comicUser, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chat_keeps_its_faithful_steps_and_a_plan_that_fails_leaves_the_comic_unplanned()
    {
        const string thread = "Maya: did you take my leftovers\nSam: define \"take\"";
        var chat = new RecordingChat(
            "not json",
            """{"title":"Leftovers","panels":[{"scene":"A fridge","caption":"","lines":[{"speaker":"Maya","text":"Where is it?","delivery":""}],"sound":""}]}""");

        var script = await new ComicWriter(NullLogger<ComicWriter>.Instance, chat).WriteAsync(thread, ComicOptions.Default, CancellationToken.None);

        Assert.Equal("Leftovers", script.Title);
        var (comicSystem, comicUser) = chat.Calls[1];
        Assert.Contains("STEP 1, work out who is who.", comicSystem, StringComparison.Ordinal);
        Assert.StartsWith("The conversation:\n<<<\n" + thread, comicUser, StringComparison.Ordinal);
    }

    /// <summary>Answers each call with the next reply and keeps the system and user text it was sent.</summary>
    private sealed class RecordingChat(params string[] replies) : IChatClient
    {
        public List<(string System, string User)> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            Calls.Add((list.First(m => m.Role == ChatRole.System).Text, list.Last(m => m.Role == ChatRole.User).Text));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, replies[Math.Min(Calls.Count - 1, replies.Length - 1)])));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
