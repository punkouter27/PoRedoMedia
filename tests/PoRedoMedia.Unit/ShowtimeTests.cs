using PoRedoMedia.Api.Features.Movies;
using PoRedoMedia.Api.Features.Video;
using PoRedoMedia.Movie.Library;
using PoRedoMedia.Api.Features.Render;
using PoRedoMedia.Movie.Render;
using PoRedoMedia.Movie.Voice;
using ApiArgs = PoRedoMedia.Api.Features.Render.FFmpegArgs;
using MovieArgs = PoRedoMedia.Movie.Render.FfmpegArgs;

namespace PoRedoMedia.UnitTests;

/// <summary>What makes a movie made of films more than its films end to end: the replay, the bed, the cuts, the timing.</summary>
public sealed class ShowtimeTests
{
    [Fact]
    public void A_films_sudden_actions_are_its_strongest_frame_changes_spread_apart()
    {
        // FFmpeg's metadata file: a frame line, then its score.
        var text = string.Join('\n', Enumerable.Range(0, 60).SelectMany(i => new[]
        {
            $"frame:{i} pts:{i} pts_time:{i / 10.0:0.0}",
            $"lavfi.scene_score={(i is 12 ? 0.6 : i is 13 ? 0.3 : i is 40 ? 0.4 : 0.01):0.000000}",
        }));
        var scores = Motion.Read(text);
        Assert.Equal(60, scores.Count);
        Assert.Equal((1.2, 0.6), scores[12]);
        // 1.3 s is on the slope of the 1.2 s peak; 4.0 s is a peak of its own; the drift underneath is nothing.
        Assert.Equal([1.2, 4.0], Motion.Peaks(scores));
        Assert.Empty(Motion.Peaks([(0, 0.01), (0.1, 0.02), (0.2, 0.01), (0.3, 0.02)]));
        Assert.Contains(Motion.Args("film.mp4", "scores.txt"), arg => arg.Contains("setpts=PTS-STARTPTS") && arg.Contains("file=scores.txt"));
    }

    [Fact]
    public void The_best_moment_is_cut_around_its_highlight_inside_the_film_and_the_replay_is_slowed_with_its_sound()
    {
        var film = TimeSpan.FromSeconds(12);
        Assert.Equal(TimeSpan.FromSeconds(3.6), MovieArgs.MomentStart(6, TimeSpan.FromSeconds(4), film)); // the moment a little past the middle
        Assert.Equal(TimeSpan.Zero, MovieArgs.MomentStart(0.5, TimeSpan.FromSeconds(4), film));
        Assert.Equal(TimeSpan.FromSeconds(8), MovieArgs.MomentStart(11.9, TimeSpan.FromSeconds(4), film));
        Assert.Equal(TimeSpan.Zero, MovieArgs.MomentStart(2, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(3)));

        var replay = string.Join(' ', MovieArgs.Moment("clip.mp4", true, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2.5), "label.txt", 2, "out.mp4"));
        Assert.Contains("setpts=2*PTS", replay);
        Assert.Contains("asetrate=24000", replay); // an octave down, as slow motion sounds
        Assert.Contains("-t 5.000", replay);
        Assert.Contains("textfile=label.txt", replay);
        var open = string.Join(' ', MovieArgs.Moment("clip.mp4", false, TimeSpan.Zero, TimeSpan.FromSeconds(4), "label.txt", 1, "out.mp4"));
        Assert.DoesNotContain("setpts", open);
        Assert.Contains("anullsrc", open); // a film with no sound gives silence
    }

    [Fact]
    public void A_music_bed_plays_only_under_the_clips_and_ducks_under_the_voice()
    {
        var args = MovieArgs.Join("clips.txt", ["a.wav", "b.wav"], "out.mp4", bed: "bed.mp3", bedSpan: (TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
        var line = string.Join(' ', args);
        Assert.Contains("-stream_loop -1 -i bed.mp3", line);
        Assert.Contains("adelay=delays=5000", line);
        Assert.Contains("atrim=0:25.000", line);
        Assert.Contains("sidechaincompress", line);
        // No bed when there is none, or when the clips are too short for one.
        Assert.DoesNotContain("sidechaincompress", string.Join(' ', MovieArgs.Join("clips.txt", ["a.wav"], "out.mp4")));
        Assert.DoesNotContain("bed.mp3", string.Join(' ', MovieArgs.Join("clips.txt", ["a.wav"], "out.mp4", bed: "bed.mp3", bedSpan: (TimeSpan.Zero, TimeSpan.FromSeconds(2)))));
    }

    [Fact]
    public void The_payoff_is_the_film_the_plan_named_else_the_last_one_watched()
    {
        var folder = Directory.CreateTempSubdirectory("showtime").FullName;
        try
        {
            Clip Film(string name, double? highlight = null, bool payoff = false)
            {
                var path = Path.Combine(folder, name);
                File.WriteAllText(path, "");
                return new Clip(Guid.NewGuid(), name, Tier.B, new Dictionary<Tier, TierScript> { [Tier.B] = new("", "", "none") },
                    new VisualSpec(VisualKind.UserVideo, KindLocked: true, UserVideoPath: path), HostVisible: false)
                { Highlight = highlight, Payoff = payoff };
            }

            Assert.Equal(1, MovieStudio.Payoff([Film("a.mp4", 2), Film("b.mp4", 3, payoff: true), Film("c.mp4", 4)]));
            Assert.Equal(1, MovieStudio.Payoff([Film("a.mp4", 2), Film("b.mp4", 3), Film("c.mp4")]));
            Assert.Equal(2, MovieStudio.Payoff([Film("a.mp4"), Film("b.mp4"), Film("c.mp4")]));
            Assert.Null(MovieStudio.Payoff([]));
            Assert.Contains("no vocals", MovieStudio.BedStyle(Show.Kids));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_roast_keeps_the_best_lines_of_twice_as_many_in_time_order()
    {
        List<(long, string, string?, string)> jokes =
        [
            (1000, "weak", "strong alt", "m1"),
            (3000, "middling", null, "m2"),
            (5000, "best", "worse alt", "m3"),
            (7000, "dud", "dud alt", "m4"),
        ];
        // Each joke's score, then its alternative's.
        double[] scores = [2, 8, 5, 9, 4, 1, 1];
        Assert.Equal([(1000L, "strong alt"), (5000L, "best")], RoastService.Best(jokes, scores, keep: 2));
        Assert.Equal("gpt-5.4", RoastService.DefaultRoastDeployment);
    }

    [Fact]
    public void Cues_snap_to_the_films_action_before_the_narrators_words()
    {
        Assert.Equal([new AudioOnset(1200, VideoStep.ImpactStrength), new AudioOnset(4000, VideoStep.ImpactStrength)], VideoStep.Impacts("1200, 4000,x,-5"));
        Assert.Empty(VideoStep.Impacts(null));
        // A zoom stays inside its window, at the frame's own size, like the other effects; there is no shake.
        Assert.Contains("crop=", ApiArgs.ResolveWindowedEffectFilter("Zoom"));
        Assert.Null(ApiArgs.ResolveWindowedEffectFilter("Shake"));
    }

    [Fact]
    public void The_narrator_pauses_after_its_sentences_so_the_roast_takes_turns_and_is_heard()
    {
        // Seven seconds of narration for a fourteen-second film (the narrator's half): three sentences.
        WordTiming W(string text, double at, double length = 0.4) => new(text, TimeSpan.FromSeconds(at), TimeSpan.FromSeconds(length));
        List<WordTiming> words = [W("The", 0), W("bottle", 0.5), W("slips.", 1.0), W("It", 2.0), W("grabs", 2.5), W("it!", 3.0), W("Then", 4.0), W("bills", 4.5), W("rain", 5.0), W("down.", 6.5)];
        var pauses = Pauses.Plan(words, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(14));
        // Seven spare seconds: one pause mid-way and the other half left after the last word, 3.5 s each.
        Assert.Single(pauses);
        Assert.Equal(3.5, pauses[0].Seconds, 3);
        Assert.True(Math.Round(pauses[0].At, 2) is 1.7 or 3.7, $"{pauses[0].At}"); // between two sentences, never inside one
        var shifted = Pauses.Shift(words, pauses);
        Assert.Equal(words[0].Start, shifted[0].Start);
        Assert.Equal(words[^1].Start + TimeSpan.FromSeconds(3.5), shifted[^1].Start);
        Assert.Empty(Pauses.Plan(words, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(8))); // too little spare time to pause
        var args = string.Join(' ', Pauses.Args("narration.wav", pauses, TimeSpan.FromSeconds(14), "out.wav"));
        Assert.Contains("aevalsrc=0:c=stereo:s=48000:d=3.5", args);
        Assert.Contains("-t 14", args);

        // The roast's lines then fit those pauses: before, a narrator who never stopped left them nowhere to go.
        var lines = shifted.Chunk(3).Select(c => ((long)(c[0].Start.TotalMilliseconds), (long)((c[^1].Start + c[^1].Duration).TotalMilliseconds))).ToList();
        RenderVisualEntry Joke(long at) => new(at, "joke.mp3", null, null, null, Voice: true, DurationMs: 3000);
        var turns = TurnTaking.Arrange([Joke(1400), Joke(10_900)], 14_000, lines);
        Assert.Equal(2, turns.Cues.Count(c => c.Voice));
        Assert.Equal(0, turns.JokesDropped);
        Assert.All(turns.Cues, j => Assert.DoesNotContain(lines, l => l.Item1 < j.TimestampMs + j.DurationMs && l.Item2 > j.TimestampMs));

        // The narrator is given half of each film to speak over.
        var note = new FootageNote("film.mp4", "", false, TimeSpan.FromSeconds(14)) { Share = MovieStudio.NarratorShareWithRoast };
        Assert.Equal(TimeSpan.FromSeconds(7), note.Narrated);
        Assert.Equal(TimeSpan.FromSeconds(14), (note with { Share = 1 }).Narrated);
        // Never under the shortest narration that can be fitted: a short film is narrated as before.
        Assert.Equal(TimeSpan.FromSeconds(5), (note with { Length = TimeSpan.FromSeconds(6) }).Narrated);
        Assert.Equal(TimeSpan.FromSeconds(4), (note with { Length = TimeSpan.FromSeconds(4) }).Narrated);
    }
}
