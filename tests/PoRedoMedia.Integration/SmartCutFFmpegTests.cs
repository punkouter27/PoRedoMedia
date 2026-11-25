using PoRedoMedia.Movie.Render;

namespace PoRedoMedia.IntegrationTests;

/// <summary>
/// The commands that put a movie together from random films, run through the real FFmpeg: a part cut at the movie's
/// shape, steadied and brightened; the measures they are decided by (brightness, black frames, when and where it was
/// filmed); and the beat of a music track.
/// </summary>
public sealed class SmartCutFFmpegTests
{
    private static readonly FfmpegRunner Runner = new(FfmpegRunner.LocateIn(Environment.GetEnvironmentVariable("PATH") ?? "") ?? "ffmpeg");

    [FFmpegFact]
    public async Task An_upright_phone_film_is_cut_into_a_wide_part_with_its_sound_and_its_facts_are_read()
    {
        var work = Directory.CreateTempSubdirectory("smartcut").FullName;
        try
        {
            var film = Path.Combine(work, "upright.mp4");
            await Runner.RunAsync(
            [
                "-f", "lavfi", "-i", "testsrc2=s=540x960:r=30:d=6", "-f", "lavfi", "-i", "sine=f=440:d=6",
                "-metadata", "creation_time=2026-07-04T17:00:00Z", "-metadata", "location=+34.0195-118.4912/",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-y", film,
            ], work, null, null, CancellationToken.None);

            var facts = FfmpegArgs.ReadFacts(await Runner.ProbeAsync(FfmpegArgs.Facts(film), CancellationToken.None));
            Assert.Equal((540, 960), (facts.Width, facts.Height));
            Assert.Equal(new DateTimeOffset(2026, 7, 4, 17, 0, 0, TimeSpan.Zero), facts.Recorded);
            Assert.Equal((34.0195, -118.4912), facts.Place!.Value);

            await Runner.RunAsync(FfmpegArgs.Luma(film, "luma.txt"), work, null, null, CancellationToken.None);
            var luma = FfmpegArgs.MetadataValues(await File.ReadAllTextAsync(Path.Combine(work, "luma.txt")), "lavfi.signalstats.YAVG");
            Assert.InRange(luma.Count, 5, 7);
            Assert.All(luma, value => Assert.InRange(value, 16, 235));

            // Upright into wide, shaky and a little dark: blurred fill, steadied, lifted.
            var graph = FfmpegArgs.Framing(facts.Width, facts.Height, Shape.Wide, 0.5, shaky: true, brightness: 0.08)!;
            var part = Path.Combine(work, "part.mp4");
            await Runner.RunAsync(FfmpegArgs.Part(film, 1.5, 3, graph, hasSound: true, part), work, TimeSpan.FromSeconds(3), null, CancellationToken.None);
            var made = FfmpegArgs.ReadFacts(await Runner.ProbeAsync(FfmpegArgs.Facts(part), CancellationToken.None));
            Assert.Equal((1280, 720), (made.Width, made.Height));
            Assert.InRange((await Runner.ProbeDurationAsync(part, CancellationToken.None)).TotalSeconds, 2.9, 3.15);
            Assert.Contains("\"audio\"", await Runner.ProbeAsync(FfmpegArgs.Facts(part), CancellationToken.None), StringComparison.Ordinal);

            // A wide film cut upright for a Short, around a subject on the right.
            var wide = FfmpegArgs.Framing(1280, 720, Shape.Tall, 0.8, false, 0)!;
            var tall = Path.Combine(work, "tall.mp4");
            await Runner.RunAsync(FfmpegArgs.Part(part, 0, 2, wide, hasSound: false, tall), work, null, null, CancellationToken.None);
            Assert.Equal((720, 1280), (FfmpegArgs.ReadFacts(await Runner.ProbeAsync(FfmpegArgs.Facts(tall), CancellationToken.None)) is var t ? (t.Width, t.Height) : default));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [FFmpegFact]
    public async Task Black_frames_are_counted_and_a_tracks_beat_is_found()
    {
        var work = Directory.CreateTempSubdirectory("smartcut").FullName;
        try
        {
            // Two seconds of black, then two of picture.
            var film = Path.Combine(work, "black.mp4");
            await Runner.RunAsync(
            [
                "-f", "lavfi", "-i", "color=c=black:s=320x240:r=30:d=2", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=30:d=2",
                "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]", "-map", "[v]", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-y", film,
            ], work, null, null, CancellationToken.None);
            await Runner.RunAsync(FfmpegArgs.Black(film, "black.txt"), work, null, null, CancellationToken.None);
            var black = FfmpegArgs.MetadataValues(await File.ReadAllTextAsync(Path.Combine(work, "black.txt")), "lavfi.blackframe.pblack").Count;
            Assert.InRange(black, 8, 12); // five frames a second are looked at: about ten black ones

            // A click every 0.5 s (120 bpm), the first at 0.25 s, for 12 seconds.
            var track = Path.Combine(work, "clicks.wav");
            await Runner.RunAsync(
            [
                "-f", "lavfi", "-i", "aevalsrc='if(lt(mod(t+0.25,0.5),0.02),sin(2*PI*1000*t),0)':s=44100:d=12", "-c:a", "pcm_s16le", "-y", track,
            ], work, null, null, CancellationToken.None);
            await Runner.RunAsync(BeatGrid.Args(track, "energy.txt"), work, null, null, CancellationToken.None);
            var levels = BeatGrid.Read(await File.ReadAllTextAsync(Path.Combine(work, "energy.txt")));
            Assert.InRange(levels.Count * BeatGrid.Hop, 11.5, 12.5);
            var beat = BeatGrid.Find(levels);
            Assert.NotNull(beat);
            Assert.InRange(beat.Value.Period, 0.49, 0.51);
            Assert.InRange(beat.Value.Phase, 0.22, 0.28);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
