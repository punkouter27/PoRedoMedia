using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Features.Comics;
using PoRedoMedia.Api.Features.Render;
using SkiaSharp;

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
        // Each speaker has a voice of their own, never the narrator's.
        Assert.Equal(2, script.Characters.Select(c => c.Voice).Distinct().Count());
        Assert.DoesNotContain(script.Characters, c => c.Voice == ComicScript.NarratorVoice);

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
        Assert.Equal("onyx", held.VoiceOf("maya"));
        Assert.NotEqual("onyx", held.VoiceOf("Sam")); // the same voice asked for twice is given once
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

        Assert.Equal((3, 2), ComicLettering.Grid(5));
        Assert.Equal((2, 2), ComicLettering.Grid(4));
        using var strip = ImageBytes.Decode(ComicLettering.Strip([lettered, lettered, lettered, lettered, lettered], "Leftover Wars"));
        Assert.True(strip.Width > strip.Height);
    }

    [Fact]
    public void Comic_command_lines_mix_the_sound_only_when_there_is_one_and_quote_paths_for_the_concat_list()
    {
        var withSound = FFmpegArgs.ComicPanelAudioArgs(["a.mp3", "b.mp3"], "boom.mp3", 0.5, 0.35, 0.8, "p.wav");
        Assert.Contains("concat=n=2:v=0:a=1,adelay=500:all=1,apad=pad_dur=0.8[speech]", withSound);
        Assert.Contains("[2:a]", withSound);
        Assert.Contains("atrim=0:3", withSound);
        Assert.Contains("amix=inputs=2:duration=first", withSound);

        var quiet = FFmpegArgs.ComicPanelAudioArgs(["a.mp3"], null, 0.5, 0.35, 0.8, "p.wav");
        Assert.DoesNotContain("amix", quiet);

        Assert.Contains("-t 3.25 ", FFmpegArgs.ComicPanelVideoArgs("p.png", "p.wav", 3.25, "c.mp4")); // never "3,25"
        Assert.Equal("file 'it'\\''s/clip.mp4'\n", FFmpegArgs.ComicConcatList(["it's/clip.mp4"]));
        Assert.DoesNotContain("-stream_loop", FFmpegArgs.ComicJoinArgs("l.txt", null, 10, "o.mp4"));
        Assert.Contains("afade=t=out:st=8.5:d=1.5", FFmpegArgs.ComicJoinArgs("l.txt", "m.mp3", 10, "o.mp4"));
    }
}
