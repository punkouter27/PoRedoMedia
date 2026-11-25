using PoRedoMedia.Movie.Library;
using PoRedoMedia.Movie.Render;
using PoRedoMedia.Movie.Script;
using PoRedoMedia.Movie.Voice;
using PoRedoMedia.Shared.Models;
using PoRedoMedia.Shared.Validation;

namespace PoRedoMedia.UnitTests;

/// <summary>
/// A movie made from films the owner did not describe: which parts are kept, in what order, which are one scene, how
/// each is framed, where the music's beats fall, and the kind of video deciding how it is told.
/// </summary>
public sealed class SmartCutTests
{
    private static WordTiming Word(string text, double start, double length) => new(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(length));

    [Fact]
    public void Best_parts_keep_the_good_stretches_at_the_paces_shot_length_and_whole_films_stay_whole()
    {
        Stretch[] seen = [new(0, 3, 2, false), new(4, 30, 9, false), new(40, 42, 6, true), new(50, 58, 3, false)];

        var parts = SmartCut.Choose(seen, 60, Pace.Medium, highlight: 20, heard: null, whole: false);
        // The dull stretches go; the long one is cut to 10 s around its best moment; the short one grows to 3.5 s.
        Assert.Equal(2, parts.Count);
        Assert.Equal((14, 24, 9), (parts[0].Start, parts[0].End, parts[0].Score));
        Assert.Equal(3.5, parts[1].Length, 3);
        Assert.True(parts[1].Shaky);

        var whole = Assert.Single(SmartCut.Choose(seen, 60, Pace.Medium, 20, null, whole: true));
        Assert.Equal((0, 60, 9), (whole.Start, whole.End, whole.Score));
        // A film nobody watched is kept whole, at the middle score.
        Assert.Equal(new Part(0, 12, SmartCut.KeepScore, false), Assert.Single(SmartCut.Choose(null, 12, Pace.Fast, null, null, whole: false)));
        // A film too short to cut a part from (here a long word would end it at half a second) stays whole.
        Assert.Equal(new Part(0, 3, 7, false), Assert.Single(SmartCut.Choose([new(0, 3, 7, false)], 3, Pace.Medium, null, [Word("sooooo", 0.5, 3.5)], false)));
        // A film where nothing scores well keeps its best stretch.
        Assert.Equal(8, Assert.Single(SmartCut.Choose([new(2, 7, 3, false), new(8, 13, 4, false)], 20, Pace.Medium, null, null, false)).Start);
    }

    [Fact]
    public void A_part_never_starts_or_ends_in_the_middle_of_a_word()
    {
        WordTiming[] heard = [Word("look", 4.8, 0.4), Word("at", 9.7, 0.6), Word("thaaaat", 13, 2)];
        var part = Assert.Single(SmartCut.Choose([new(5, 10, 8, false)], 30, Pace.Medium, null, heard, whole: false));
        Assert.Equal(4.75, part.Start, 3); // moved back to where "look" starts
        Assert.Equal(10.4, part.End, 3);   // moved on to just after "at" ends
        Assert.Equal(13, SmartCut.SnapEnd(14, heard, 30)); // a long word ends too far on: back to before it
    }

    [Fact]
    public void The_best_parts_that_fit_are_kept_in_their_order()
    {
        (string Name, double Length, int Score)[] parts = [("a", 10, 5), ("b", 20, 9), ("c", 15, 7), ("d", 5, 2)];
        Assert.Equal(["b", "c", "d"], SmartCut.Fit(parts, p => p.Length, p => p.Score, budget: 40, most: 20).Select(p => p.Name));
        Assert.Equal(["b", "c"], SmartCut.Fit(parts, p => p.Length, p => p.Score, budget: 100, most: 2).Select(p => p.Name));
        Assert.Equal(["b"], SmartCut.Fit(parts, p => p.Length, p => p.Score, budget: 5, most: 20).Select(p => p.Name)); // the best always stays
    }

    [Fact]
    public void People_talking_keep_their_own_sound_and_a_part_gets_its_own_words_and_timeline()
    {
        WordTiming[] heard = [Word("hi", 1, 0.3), Word("there", 1.5, 0.3), Word("look", 2, 0.3), Word("wow", 2.6, 0.3), Word("later", 20, 0.4)];
        Assert.True(SmartCut.Talking(heard, 0.5, 3.5));
        Assert.False(SmartCut.Talking(heard, 5, 25));
        Assert.False(SmartCut.Talking(null, 0, 10));
        var sliced = SmartCut.Slice(heard, 1.5, 3)!;
        Assert.Equal(["there", "look", "wow"], sliced.Select(w => w.Text));
        Assert.Equal(TimeSpan.Zero, sliced[0].Start);

        var timeline = "0-4 s: walks in\n4-9 s: trips on the rug\n9-15 s: laughs it off";
        Assert.Equal("0-3 s: trips on the rug\n3-8 s: laughs it off", SmartCut.Slice(timeline, 6, 14));
        Assert.StartsWith("(This clip is seconds 6-14 of the film.)", SmartCut.Slice("A dog runs about.", 6, 14), StringComparison.Ordinal);
    }

    [Fact]
    public void Films_filmed_close_together_are_one_scene_and_trips_run_in_time_order()
    {
        var day = new DateTimeOffset(2026, 7, 4, 10, 0, 0, TimeSpan.Zero);
        var beach = (34.0, -118.5);
        var park = (34.1, -118.3); // about 20 km away
        FilmFacts[] films =
        [
            new(1920, 1080, day.AddMinutes(90), park),
            new(1920, 1080, day, beach),
            new(1920, 1080, day.AddMinutes(5), beach),
            new(1920, 1080, day.AddMinutes(95), park),
        ];
        var (scene, byTime) = Arrange.Scenes(films);
        Assert.Equal([1, 2, 0, 3], byTime);
        Assert.Equal([1, 0, 0, 1], scene);
        Assert.Equal([1, 2, 0, 3], Arrange.Order(films, [5, 5, 5, 5], timeOrder: true));
        // A party opens on its strongest scene (the park, with the 9), then the rest build up.
        Assert.Equal([0, 3, 1, 2], Arrange.Order(films, [9, 3, 4, 5], timeOrder: false));

        // Without times, each film is its own scene; one scene orders its films strongest first, then building up.
        FilmFacts[] unknown = [new(1, 1, null, null), new(1, 1, null, null), new(1, 1, null, null), new(1, 1, null, null)];
        Assert.Equal([0, 1, 2, 3], Arrange.Scenes(unknown).Scene);
        Assert.Equal([2, 3, 0, 1], Arrange.Order(unknown, [4, 6, 9, 2], timeOrder: false));
        Assert.Equal([0, 1, 2, 3], Arrange.Order(unknown, [4, 6, 9, 2], timeOrder: true));
        Assert.InRange(Arrange.Kilometres(beach, park), 15, 25);
    }

    [Fact]
    public void A_written_script_is_held_to_the_show_order_and_the_replay_comes_from_the_best_part()
    {
        FootageNote Note(string path, int score) => new(path, "", false, TimeSpan.FromSeconds(8)) { Score = score };
        Clip Clip(string path, string beat) => new(Guid.NewGuid(), path, Tier.B, new Dictionary<Tier, TierScript> { [Tier.B] = new("words", "", "none") },
            new VisualSpec(VisualKind.UserVideo, KindLocked: true, UserVideoPath: path), HostVisible: false)
        { Beat = beat };
        FootageNote[] order = [Note("a.mp4", 9), Note("b.mp4", 3), Note("c.mp4", 8), Note("d.mp4", 4)];
        // The model wrote them out of order.
        var written = new Episode("t", "topic", [Clip("c.mp4", "Opening"), Clip("a.mp4", "Highlights"), Clip("d.mp4", "Highlights"), Clip("b.mp4", "Closing")], 1)
        { Structure = BeatStructure.Movie };

        var shown = SmartCut.InShowOrder(written, order);
        Assert.Equal(["a.mp4", "b.mp4", "c.mp4", "d.mp4"], shown.Clips.Select(c => c.Visual.UserVideoPath));
        Assert.Equal(["Opening", "Highlights", "Highlights", "Closing"], shown.Clips.Select(c => c.Beat)); // each place keeps its beat
        // The opening shot (the 9) is seen first already, so the replay is the next best.
        Assert.Equal("c.mp4", Assert.Single(shown.Clips, c => c.Payoff).Visual.UserVideoPath);
    }

    [Fact]
    public void A_steady_beat_is_found_and_cuts_move_back_onto_it()
    {
        // A click every half second (120 bpm), the first at 0.2 s, over a little noise.
        var random = new Random(7);
        var levels = Enumerable.Range(0, (int)(20 / BeatGrid.Hop)).Select(i =>
        {
            var t = i * BeatGrid.Hop;
            var sinceBeat = (t - 0.2 + 0.5) % 0.5;
            return (sinceBeat < 0.03 ? 1.0 : 0.1) + random.NextDouble() * 0.02;
        }).ToList();
        var beat = BeatGrid.Find(levels)!.Value;
        Assert.InRange(beat.Period, 0.495, 0.505);
        Assert.InRange(beat.Phase, 0.18, 0.22);
        Assert.Null(BeatGrid.Find([.. Enumerable.Repeat(0.3, 3000)])); // no beat at all
        Assert.Null(BeatGrid.Find([1, 0, 1, 0]));                    // far too short to tell

        // A 10 s track played again from its start: its beats start over with it.
        var beats = BeatGrid.Beats((0.5, 0.2), 10, 21);
        Assert.Equal(0.2, beats[0], 3);
        Assert.Contains(beats, b => Math.Abs(b - 10.2) < 1e-9);
        Assert.DoesNotContain(beats, b => b > 21);

        // The first cut, at 4.85 s (each clip overlaps the next by 0.15 s), moves back to the beat at 4.7; the second, then
        // at 4.7 + 4.9 - 0.15 = 9.45, back to the beat at 9.2.
        var trims = BeatGrid.Trims([5, 4.9, 5], 0.15, beats, most: 0.3, shortest: 1.5);
        Assert.Equal(0.15, trims[0], 3);
        Assert.Equal(0.25, trims[1], 3);
        Assert.Equal(0, trims[2]); // nothing follows the last clip
        Assert.Equal(0, BeatGrid.Trims([5, 5], 0.15, [1.0], most: 0.3, shortest: 1.5)[0]); // no beat near enough: left alone
    }

    [Fact]
    public void Mixed_footage_is_framed_to_the_movies_shape_steadied_and_brightened()
    {
        // An upright phone film in a wide movie: shown whole over a blurred copy of itself.
        var upright = FfmpegArgs.Framing(1080, 1920, Shape.Wide, 0.5, shaky: false, brightness: 0)!;
        Assert.Contains("gblur", upright);
        Assert.Contains("overlay=(W-w)/2:(H-h)/2", upright);
        // A wide film in a Short: cropped upright around its subject, on the right here.
        var tall = FfmpegArgs.Framing(1920, 1080, Shape.Tall, 0.8, shaky: true, brightness: 0.06)!;
        Assert.Contains("crop=720:1280:x='max(0,min(iw-ow,0.8*iw-ow/2))'", tall);
        Assert.Contains("deshake", tall);
        Assert.Contains("eq=brightness=0.06", tall);
        Assert.EndsWith("[v]", tall, StringComparison.Ordinal);
        // A film already fitting, steady and evenly lit needs nothing, unless it has to be cut anyway.
        Assert.Null(FfmpegArgs.Framing(1920, 1080, Shape.Wide, 0.5, false, 0));
        Assert.NotNull(FfmpegArgs.Framing(1920, 1080, Shape.Wide, 0.5, false, 0, always: true));

        Assert.Equal(0, FfmpegArgs.Brightness(105, 110));
        Assert.True(FfmpegArgs.Brightness(60, 110) > 0);
        Assert.Equal(-0.1, FfmpegArgs.Brightness(250, 110));
        var part = string.Join(' ', FfmpegArgs.Part("film.mp4", 4, 6.5, upright, hasSound: false, "out.mp4"));
        Assert.Contains("-ss 4 -t 6.5 -i film.mp4", part);
        Assert.Contains("-an", part);
    }

    [Fact]
    public void A_phones_film_says_when_and_where_it_was_filmed_and_is_turned_upright()
    {
        const string Json = """
            {"streams":[{"codec_type":"video","width":1920,"height":1080,"side_data_list":[{"rotation":-90}]},{"codec_type":"audio"}],
             "format":{"tags":{"creation_time":"2026-07-04T17:00:05.000000Z","com.apple.quicktime.creationdate":"2026-07-04T10:00:00-0700","com.apple.quicktime.location.ISO6709":"+34.0195-118.4912+012.000/"}}}
            """;
        var facts = FfmpegArgs.ReadFacts(Json);
        Assert.Equal((1080, 1920), (facts.Width, facts.Height));
        Assert.True(facts.Portrait);
        Assert.Equal(new DateTimeOffset(2026, 7, 4, 17, 0, 0, TimeSpan.Zero), facts.Recorded!.Value.ToUniversalTime());
        Assert.Equal((34.0195, -118.4912), facts.Place!.Value);

        var plain = FfmpegArgs.ReadFacts("""{"streams":[{"codec_type":"video","width":1280,"height":720}],"format":{}}""");
        Assert.Equal(new FilmFacts(1280, 720, null, null), plain);
    }

    [Fact]
    public void The_kind_of_video_decides_its_series_pace_music_and_order()
    {
        Assert.Equal((Show.General, BeatStructure.DayOut), VideoKinds.SeriesOf(VideoKind.Trip));
        Assert.Equal((Show.Kids, BeatStructure.Highlights), VideoKinds.SeriesOf(VideoKind.Kids));
        Assert.Equal(Pace.Fast, VideoKinds.PaceOf(VideoKind.Sport));
        Assert.Equal("calm", VideoKinds.MoodOf(VideoKind.Trip));
        Assert.True(VideoKinds.InTimeOrder(VideoKind.HowTo));
        Assert.False(VideoKinds.InTimeOrder(VideoKind.Party));
        Assert.Equal(VideoKind.HowTo, VideoKinds.Parse("howto"));
        Assert.Null(VideoKinds.Parse("opera"));
        Assert.Equal(VideoKind.Trip, VideoKinds.Of(BeatStructure.DayOut));
    }

    [Fact]
    public void Best_parts_and_the_format_are_checked_with_the_rest_of_the_form()
    {
        var rules = new MovieLessonRequestValidator();
        var limits = new MovieLimits(MaxFilms: 5, MaxTopicLength: 100);
        var request = new MovieLessonRequest("General", "", "", "", 1, [new MovieFilmDto(Guid.NewGuid(), "", false)]);
        Assert.True(rules.Validate(request with { Auto = true, Cut = "best", Format = "short" }, limits).IsValid);
        Assert.True(rules.Validate(request, limits).IsValid);
        Assert.Equal(nameof(MovieLessonRequest.Cut), Assert.Single(rules.Validate(request with { Cut = "most" }, limits).Errors).PropertyName);
        Assert.Equal(nameof(MovieLessonRequest.Format), Assert.Single(rules.Validate(request with { Format = "square" }, limits).Errors).PropertyName);
    }

    [Fact]
    public void A_saved_movie_keeps_its_scenes_mood_and_that_the_app_put_it_together()
    {
        var clip = new Clip(Guid.NewGuid(), "x", Tier.B, new Dictionary<Tier, TierScript> { [Tier.B] = new("", "", "none") }, new VisualSpec(VisualKind.TitleCard), false) { Scene = 2 };
        var episode = new Episode("t", "topic", [clip], 1) { Mood = "calm", AutoEdit = true };
        var back = Json.Read<Episode>(Json.Write(episode))!;
        Assert.Equal(2, back.Clips[0].Scene);
        Assert.Equal(("calm", true), (back.Mood, back.AutoEdit));
        // One saved before these were kept reads as before.
        var old = Json.Read<Episode>(Json.Write(new Episode("t", "topic", [clip with { Scene = null }], 1)))!;
        Assert.Equal((null, false, (int?)null), (old.Mood, old.AutoEdit, old.Clips[0].Scene));
    }
}
