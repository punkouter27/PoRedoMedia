using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Features.Comics;
using PoRedoMedia.Api.Features.Render;
using PoRedoMedia.Shared.Models;
using SkiaSharp;
using static PoRedoMedia.Api.Features.Comics.ComicLettering;

namespace PoRedoMedia.UnitTests;

public sealed class ComicTests
{
    private const string Thread =
        "[10:42] Maya: did you take my leftovers\n"
        + "Sam (10:43): define \"take\"\n"
        + "> Quoted earlier mail is skipped\n"
        + "Maya: SAM\n"
        + "it was pad thai\n"
        + "Sam: I'm buying you two";

    [Fact]
    public void The_stand_in_script_keeps_the_real_lines_and_a_short_or_long_draft_is_held_to_three_to_eight_panels()
    {
        var script = ComicScript.Normalize(ComicScript.Fallback(Thread), Thread);

        var lines = script.Panels.SelectMany(p => p.Lines).ToList();
        Assert.Equal(["Maya", "Sam", "Maya", "Sam"], lines.Select(l => l.Speaker));
        Assert.Equal("SAM it was pad thai", lines[2].Text); // an unnamed row continues the last speaker
        Assert.DoesNotContain(lines, l => l.Text.Contains("Quoted", StringComparison.Ordinal));
        Assert.InRange(script.Panels.Count, ComicScript.MinPanels, ComicScript.MaxPanels);
        Assert.Single(script.Panels, p => p.Laugh && p.Applause); // the stand-in laughs and claps once, at the end
        var laughsEverywhere = ComicScript.Normalize(ComicScript.Fallback(Thread) with
            { Panels = [.. Enumerable.Range(0, 8).Select(i => new ComicPanelDraft("s", "", [new("Maya", "joke", "")], "", true))] }, Thread);
        Assert.Equal(ComicOptions.Default.MaxLaughs, laughsEverywhere.Panels.Count(p => p.Laugh));
        // Each speaker has a voice of their own, never the narrator's.
        Assert.Equal(2, script.Characters.Select(c => c.Voice).Distinct().Count());
        Assert.DoesNotContain(script.Characters, c => c.Voice == ComicOptions.Default.NarratorVoice.Voice);

        // An email chain, newest first, is read by its From headers and put back in the order it was sent.
        const string chain =
            "From: Sam Ortiz <sam@example.com>\nSent: Tuesday\nSubject: RE: leftovers\nFine. Two pad thais.\nSent from my iPhone\n\n"
            + "On Mon, Maya wrote:\n> quoted\n"
            + "From: Maya Lopez <maya@example.com>\nTo: sam@example.com\nWho ate my lunch?\nIt had a label.";
        Assert.Equal(
            [("Maya Lopez", "Who ate my lunch? It had a label."), ("Sam Ortiz", "Fine. Two pad thais.")],
            ComicScript.ParseLines(chain).Select(l => (l.Speaker!, l.Text!)));

        var tooMany = new ComicDraft("T", "S", "", [new("Maya", "red coat", "onyx"), new("Sam", "", "onyx")],
            [.. Enumerable.Range(0, 12).Select(i => new ComicPanelDraft($"scene {i}", "", [new("Maya", "hi", ""), new("Sam", "yo", "")], ""))]);
        var held = ComicScript.Normalize(tooMany, Thread);
        Assert.Equal(ComicScript.MaxPanels, held.Panels.Count);
        Assert.Equal("onyx", held.VoiceOf("maya", ComicOptions.Default));
        Assert.NotEqual("onyx", held.VoiceOf("Sam", ComicOptions.Default)); // the same voice asked for twice is given once

        // The sliders hold the draft: Few meme sounds is one, the heckler only when on and only as often as its lines
        // level allows, and its voice and the narrator's never go to a character. A description is cut from a name.
        var busy = new ComicDraft("T", "S", "", [new("Partner (car driver)", "", "verse")],
            [.. Enumerable.Range(0, 8).Select(i => new ComicPanelDraft("s", "", [new("Partner (car driver)", "hi", "")], "boom", false, "nice one"))]);
        var few = ComicOptions.Default with { Mix = ComicRequest.DefaultMix with { Sounds = 1, Jokes = 1 }, Heckler = true };
        var options = few with { Narrator = new NarratorChoice("deadpan", 4) };
        var mixed = ComicScript.Normalize(busy, Thread, options);
        Assert.Equal(1, mixed.Panels.Count(p => p.Sound.Length > 0));
        Assert.Equal(2, mixed.Panels.Count(p => p.Heckle.Length > 0)); // Few: a quarter of eight panels
        Assert.Equal(0, ComicScript.Normalize(busy, Thread, ComicOptions.Default with { Heckler = false }).Panels.Count(p => p.Heckle.Length > 0));
        Assert.Equal("Partner", Assert.Single(mixed.Characters).Name);
        Assert.NotEqual("onyx", mixed.Characters[0].Voice); // the deadpan narrator's
        Assert.NotEqual("verse", mixed.Characters[0].Voice); // the heckler's, asked for by the draft
        Assert.Contains("Heckler: nice one", mixed.Transcript());

        // The estimate: four messages are two panels, which is held to three.
        var prices = new AiPricingDto(0, 0.01m, 0.04m, 0, 0, 0.002m);
        // The speaker finder keeps only the speakers asked about, inside the picture.
        var found = ComicSpeakerLocator.Spots(new([new("maya", 0.2f, 0.5f), new("Stranger", 0.5f, 0.5f), new("Sam", 1.4f, -0.2f)]),
            [new("Maya", "", "onyx"), new("Sam", "", "echo")]);
        Assert.Equal(new SkiaSharp.SKPoint(0.2f, 0.5f), found["Maya"]);
        Assert.Equal(new SkiaSharp.SKPoint(1f, 0f), found["Sam"]);
        Assert.Equal(2, found.Count);

        Assert.Equal((0.14m, 0.89m), ComicRequest.Estimate("a: 1\nb: 2\na: 3\nb: 4", false, prices));
    }

    [Fact]
    public void A_panel_is_lettered_square_with_bubbles_up_top_and_the_strip_lays_panels_out_in_rows()
    {
        using var art = new SKBitmap(1200, 800);
        art.Erase(new SKColor(20, 40, 160));
        var panel = new ComicPanel("scene", "Meanwhile, in the kitchen", [new ComicLine("Maya", "Did you take my leftovers?", ""), new ComicLine("Sam", "Define take.", "")], "");

        var lettered = ComicLettering.LetterPanel(ImageBytes.ToPng(art), panel);

        using var image = ImageBytes.Decode(lettered);
        Assert.Equal((ComicLettering.PanelSize, ComicLettering.PanelSize), (image.Width, image.Height));
        int White(int fromY, int toY) => Enumerable.Range(fromY, toY - fromY)
            .Sum(y => Enumerable.Range(20, image.Width - 40).Count(x => image.GetPixel(x, y) is { Red: > 240, Green: > 240, Blue: > 240 }));
        Assert.True(White(20, image.Height / 2) > 5000, "the bubbles are drawn in the upper half");
        Assert.Equal(0, White(image.Height * 3 / 4, image.Height - 20)); // the lower picture is left clear

        // The video reveals the parts one at a time: with none shown, the picture is bare.
        Assert.Equal(3, ComicLettering.Parts(panel));
        using (var bare = ImageBytes.Decode(ComicLettering.LetterPanel(ImageBytes.ToPng(art), panel, shown: 0)))
            Assert.DoesNotContain(Enumerable.Range(40, 400), y => bare.GetPixel(PanelSize / 2, y) is { Red: > 240, Green: > 240, Blue: > 240 });

        // Two panels across keeps them, and their words, large.
        // Found in the picture, Maya (who speaks first) on the right: her bubble sits over her, its tail reaching down there.
        var located = ComicLettering.LetterPanel(ImageBytes.ToPng(art), panel with { Caption = "" },
            spots: new Dictionary<string, SKPoint> { ["Maya"] = new(0.75f, 0.6f), ["Sam"] = new(0.25f, 0.65f) });
        using (var tails = ImageBytes.Decode(located))
        {
            int Lettered(int fromX, int toX, int atY) => Enumerable.Range(fromX, toX - fromX).Count(x => tails.GetPixel(x, atY) is { Red: > 240, Green: > 240, Blue: > 240 });
            Assert.True(Lettered(PanelSize / 2, PanelSize - 40, 80) > Lettered(40, PanelSize / 2, 80), "Maya's bubble is on her side");
        }

        Assert.Equal((2, 3), ComicLettering.Grid(5));
        Assert.Equal((2, 2), ComicLettering.Grid(4));
        using var strip = ImageBytes.Decode(ComicLettering.Strip([lettered, lettered, lettered, lettered, lettered], "Leftover Wars"));
        Assert.True(strip.Height > strip.Width);
    }

    [Fact]
    public void Comic_command_lines_mix_the_sound_only_when_there_is_one_and_quote_paths_for_the_concat_list()
    {
        var withSound = FFmpegArgs.ComicPanelAudioArgs(["a.mp3", "b.mp3"], "boom.mp3", null, 0.5, 0.35, 0.8, "p.wav");
        Assert.Contains("concat=n=2:v=0:a=1,adelay=500:all=1,apad=pad_dur=0.8[speech]", withSound);
        Assert.Contains("[2:a]", withSound);

        // A laugh is said once, after the last line, cut short: never looped, never under a line.
        var punchline = FFmpegArgs.ComicPanelAudioArgs(["a.mp3"], "boom.mp3", "laugh.mp3", 0.5, 0.35, 0.8, "p.wav");
        Assert.Contains("[1:a]aresample=44100,aformat=sample_fmts=fltp:channel_layouts=stereo,atrim=0:3.5", punchline);
        Assert.Contains("[l0][r0]concat=n=2", punchline);
        var bigFinish = FFmpegArgs.ComicPanelAudioArgs(["a.mp3"], null, "laugh.mp3", 0.5, 0.35, 0.8, "p.wav", applause: "clap.wav");
        Assert.Contains("[l0][r0][r1]concat=n=3", bigFinish); // the laugh, then the applause
        Assert.Contains("[2:a]", punchline); // the meme sound comes after the laugh's input
        Assert.DoesNotContain("loop", punchline);
        Assert.Contains("atrim=0:3", withSound);
        Assert.Contains("amix=inputs=2:duration=first", withSound);

        var quiet = FFmpegArgs.ComicPanelAudioArgs(["a.mp3"], null, null, 0.5, 0.35, 0.8, "p.wav");
        Assert.DoesNotContain("amix", quiet);

        var video = FFmpegArgs.ComicPanelVideoArgs([("bare.png", 0.5), ("one.png", 2.75)], "p.wav", "c.mp4");
        Assert.Contains("-t 3.25 ", video); // the stills' total, and never "3,25"
        Assert.Contains("[0:v][1:v]concat=n=2:v=1:a=0", video);
        Assert.Contains("[2:a]apad[a]", video);
        // The bare picture holds for the lead, each part for itself and the gap, the last to the panel's end.
        Assert.Equal([0.5, 2.35, 3.15], FFmpegArgs.ComicStillSeconds([2.0, 1.5], 0.5, 0.35, 6.0).Select(t => Math.Round(t, 2)));
        Assert.Equal([4.0], FFmpegArgs.ComicStillSeconds([], 0.5, 0.35, 4.0));
        Assert.Equal("file 'it'\\''s/clip.mp4'\n", FFmpegArgs.ComicConcatList(["it's/clip.mp4"]));
        Assert.DoesNotContain("-stream_loop", FFmpegArgs.ComicJoinArgs("l.txt", null, 10, "o.mp4"));
        Assert.Contains("afade=t=out:st=8.5:d=1.5", FFmpegArgs.ComicJoinArgs("l.txt", "m.mp3", 10, "o.mp4"));
    }
}
