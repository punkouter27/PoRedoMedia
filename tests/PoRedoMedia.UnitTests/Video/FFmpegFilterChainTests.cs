using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Features.Render;
using PoRedoMedia.Api.Features.Video;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.UnitTests;

/// <summary>
/// Covers the pure string building in <see cref="FFmpegArgs"/>. This is the
/// highest-risk logic in the render path — a malformed filter chain fails at the ffmpeg
/// process boundary, where the only signal is a non-zero exit code and a stderr dump.
/// </summary>
public sealed class FFmpegFilterChainTests
{
    private static RenderVisualEntry Sound(long ms = 1000, string? effect = null, string? caption = null, string? position = null)
        => new(ms, "/tmp/sound.mp3", effect, caption, position);

    // ── Aspect-ratio framing ──────────────────────────────────────────────

    [Fact]
    public void SocialArgs_FitTheWholePictureOnABlurredFillOfTheChosenFrameAndRefuseAnyOtherAspect()
    {
        foreach (var (aspect, frame) in new[] { (SocialExport.Vertical, "720:1280"), (SocialExport.Square, "720:720"), (SocialExport.Wide, "1280:720") })
        {
            var args = FFmpegArgs.SocialArgs("https://store/v.mp4?sig=1", "/tmp/out.mp4", aspect)!;

            Assert.StartsWith("-v error -i \"https://store/v.mp4?sig=1\" -filter_complex \"[0:v]split[a][b];", args, StringComparison.Ordinal);
            // The fill is cropped to the frame; the picture itself only shrinks to fit, so nothing is cut off.
            Assert.Contains($"[a]scale={frame}:force_original_aspect_ratio=increase,crop={frame},boxblur=", args, StringComparison.Ordinal);
            Assert.Contains($"[b]scale={frame}:force_original_aspect_ratio=decrease:force_divisible_by=2[fg]", args, StringComparison.Ordinal);
            Assert.Contains("[bg][fg]overlay=(W-w)/2:(H-h)/2", args, StringComparison.Ordinal);
            // A silent clip has no audio stream, so its mapping is optional.
            Assert.Contains("-map \"[v]\" -map 0:a? -c:v libx264", args, StringComparison.Ordinal);
            Assert.EndsWith("-movflags +faststart -y \"/tmp/out.mp4\"", args, StringComparison.Ordinal);
        }

        // The aspect becomes part of a blob path, so only the three offered ones are accepted.
        Assert.Null(FFmpegArgs.SocialArgs("in", "out", "4x5"));
        Assert.Null(FFmpegArgs.SocialArgs("in", "out", "../x"));
        Assert.Null(FFmpegArgs.SocialArgs("in", "out", null));
    }

    // ── Visual effects ────────────────────────────────────────────────────

    [Fact]
    public void BuildVideoFilterChain_AggressiveVisuals_AddsDeepFryAndUnsharp()
    {
        var chain = FFmpegArgs.BuildVideoFilterChain([Sound()], aggressiveVisuals: true);

        Assert.Contains("eq=saturation=3", chain, StringComparison.Ordinal);
        Assert.Contains("unsharp=", chain, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFFmpegArgs_RepeatedCueEffect_GetsOneWindowedPassPerCue()
    {
        var entries = new List<RenderVisualEntry>
        {
            new(TimestampMs: 1000, SoundPath: "/tmp/a.mp3", VisualEffect: "MotionBlur", CaptionText: null, CaptionPosition: null),
            new(TimestampMs: 2000, SoundPath: "/tmp/b.mp3", VisualEffect: "MotionBlur", CaptionText: null, CaptionPosition: null)
        };

        var args = FFmpegArgs.BuildFFmpegArgs(
            sourcePath: "/tmp/source.mp4",
            entries: entries,
            outputPath: "/tmp/out.mp4",
            aggressiveVisuals: false,
            sourceDurationSeconds: 10.0,
            sourceHasAudio: true);

        // One pass per cue — not deduplicated per effect, since each needs its own window.
        Assert.Equal(2, args.Split("tblend=all_mode=average").Length - 1);
        Assert.Contains("between(t\\,1\\,3.5)", args, StringComparison.Ordinal);
        Assert.Contains("between(t\\,2\\,4.5)", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFFmpegArgs_RoastVoice_and_meme_sounds_share_one_loudness_and_only_the_source_ducks()
    {
        var entries = new List<RenderVisualEntry>
        {
            new(TimestampMs: 1000, SoundPath: "/tmp/boom.mp3", VisualEffect: null, CaptionText: null, CaptionPosition: null, GainDb: -3.4),
            new(TimestampMs: 3000, SoundPath: "/tmp/joke.mp3", VisualEffect: null, CaptionText: null, CaptionPosition: null, Voice: true, GainDb: 2.5)
        };

        var args = FFmpegArgs.BuildFFmpegArgs("/tmp/source.mp4", entries, "/tmp/out.mp4", aggressiveVisuals: false, sourceDurationSeconds: 10.0, sourceHasAudio: true);

        // Each clip carries its own measured gain to the shared target; no fixed boost or cut.
        Assert.Contains("[1:a]adelay=1000|1000,volume=-3.4dB[a0]", args, StringComparison.Ordinal);
        Assert.Contains("[2:a]adelay=3000|3000,volume=2.5dB[a1]", args, StringComparison.Ordinal);
        // The voice ducks the source only; the meme sound joins the final mix at its own level.
        Assert.Contains("[aorig][voxkey]sidechaincompress", args, StringComparison.Ordinal);
        Assert.Contains("[ducked][a0][vox]amix=inputs=3", args, StringComparison.Ordinal);
    }

    [Fact]
    public void Meme_sounds_and_jokes_take_turns_a_waiting_sound_leaves_its_caption_and_a_song_mutes_sounds()
    {
        RenderVisualEntry Sound(long at, int ms, string? caption = null) => new(at, "/s.mp3", null, caption, null, DurationMs: ms);
        RenderVisualEntry Joke(long at, int ms) => new(at, "/j.mp3", null, null, null, Voice: true, DurationMs: ms);
        static (long, bool, bool, string?) Seen(RenderVisualEntry c) => (c.TimestampMs, c.Voice, c.SoundPath.Length > 0, c.CaptionText);

        var result = TurnTaking.Arrange(
            [Sound(1000, 2000, "A"), Sound(4000, 1000, "B"), Sound(6000, 1000), Sound(8000, 1000), Joke(1000, 3000), Joke(15000, 2000), Joke(19000, 2000)],
            videoMs: 20_000);

        // The first joke lets the sound at its moment finish. The sounds that fall in the joke wait
        // for it, one after another; "B" stays on screen at its moment while its sound waits.
        Assert.Equal(
            [(1000L, false, true, "A"), (3200L, true, true, null), (4000L, false, false, "B"), (6400L, false, true, null),
             (7600L, false, true, null), (8800L, false, true, null), (15000L, true, true, null)],
            result.Cues.Select(Seen));
        Assert.Equal((1, 1, 3, 0), (result.JokesMoved, result.JokesDropped, result.SoundsWaited, result.SoundsLeftOut));

        // A song (the rap) mutes the meme sounds under it; their captions still show.
        var song = TurnTaking.Arrange([Joke(0, 40_000), Sound(5000, 1000, "C"), Sound(42_000, 1000)], videoMs: 45_000);
        Assert.Equal([(0L, true, true, null), (5000L, false, false, "C"), (42_000L, false, true, null)], song.Cues.Select(Seen));
    }

    // ── Captions ──────────────────────────────────────────────────────────

    [Fact]
    public void BuildVideoFilterChain_Caption_IsUppercasedAndTimeWindowed()
    {
        var chain = FFmpegArgs.BuildVideoFilterChain(
            [Sound(2000, caption: "oh no")], aggressiveVisuals: false);

        Assert.Contains("drawtext=text='OH NO'", chain, StringComparison.Ordinal);
        // 2.5 s display window starting at the cue timestamp.
        Assert.Contains("between(t\\,2\\,4.5)", chain, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildVideoFilterChain_BlankCaption_AddsNoDrawtext()
    {
        var chain = FFmpegArgs.BuildVideoFilterChain(
            [Sound(caption: "   ")], aggressiveVisuals: false);

        Assert.DoesNotContain("drawtext", chain, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildVideoFilterChain_TimestampsUseInvariantCulture()
    {
        // A comma decimal separator here would split the filter chain, since ',' is the
        // filtergraph separator.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var chain = FFmpegArgs.BuildVideoFilterChain(
                [Sound(1500, caption: "hi")], aggressiveVisuals: false);

            Assert.Contains("between(t\\,1.5\\,4)", chain, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void BuildVideoFilterChain_FiltersAreCommaJoined()
    {
        var chain = FFmpegArgs.BuildVideoFilterChain(
            [Sound(effect: "MotionBlur")], aggressiveVisuals: true);

        // scale, eq, unsharp → three filters, two separators. Escaped commas
        // (\,) inside a filter's own arguments do not separate filters, so they are
        // neutralised before splitting.
        var filters = chain.Replace("\\,", "").Split(',');
        Assert.Equal(3, filters.Length);
        Assert.StartsWith("scale=", filters[0], StringComparison.Ordinal);
        Assert.StartsWith("eq=", filters[1], StringComparison.Ordinal);
        Assert.StartsWith("unsharp=", filters[2], StringComparison.Ordinal);
    }

    // ── drawtext escaping ─────────────────────────────────────────────────

    [Fact]
    public void SanitizeForDrawtext_FlattensNewlinesToSpaces()
    {
        Assert.Equal("a b", FFmpegArgs.SanitizeForDrawtext("a\r\nb"));
    }

    [Fact]
    public void SanitizeForDrawtext_DoublesBackslashThenDropsQuote()
    {
        // Order matters: the backslash pass runs first, so the caller's own backslash is
        // doubled and the apostrophe is then dropped, leaving \\ and nothing else. Running
        // the backslash pass last would instead double any backslash a later escape pass
        // introduced, turning \: back into \\:.
        Assert.Equal(@"\\", FFmpegArgs.SanitizeForDrawtext(@"\'"));
    }

    // ── Windowed cue effects & Overlay filters ────────────────────────────

    [Fact]
    public void BuildFFmpegArgs_WithWindowedEffectAndOverlay_ChainsFiltersAndInputs()
    {
        var entries = new List<RenderVisualEntry>
        {
            new(
                TimestampMs: 1000,
                SoundPath: "/tmp/boom.mp3",
                VisualEffect: "MotionBlur",
                CaptionText: "BOOM",
                CaptionPosition: "Top",
                OverlayPath: "/tmp/deal-with-it.png",
                OverlayX: 0.3,
                OverlayY: 0.4,
                OverlayScale: 1.2)
        };

        var args = FFmpegArgs.BuildFFmpegArgs(
            sourcePath: "/tmp/source.mp4",
            entries: entries,
            outputPath: "/tmp/out.mp4",
            aggressiveVisuals: false,
            sourceDurationSeconds: 10.0,
            sourceHasAudio: true);

        // Inputs should include source, sound, and overlay
        Assert.Contains("-i \"/tmp/source.mp4\"", args, StringComparison.Ordinal);
        Assert.Contains("-i \"/tmp/boom.mp3\"", args, StringComparison.Ordinal);
        Assert.Contains("-i \"/tmp/deal-with-it.png\"", args, StringComparison.Ordinal);

        // Filter complex should chain split, the effect, and a time-gated overlay
        Assert.Contains("split", args, StringComparison.Ordinal);
        Assert.Contains("tblend=all_mode=average", args, StringComparison.Ordinal);
        Assert.Contains("overlay=", args, StringComparison.Ordinal);
        // The effect must be confined to its cue's window, not smeared over the whole video.
        Assert.Contains("overlay=enable='between(t\\,1\\,3.5)'", args, StringComparison.Ordinal);
        Assert.Contains("-map \"[vout]\"", args, StringComparison.Ordinal);
        Assert.Contains("-map \"[aout]\"", args, StringComparison.Ordinal);
    }
}
