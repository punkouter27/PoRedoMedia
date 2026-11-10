using System.Globalization;

namespace PoRedoMedia.Movie.Render;

// Sound, and putting the finished clips together: each slot's sound, a film's own, the outro music and the join.
public static partial class FfmpegArgs
{
    /// <summary>A picture and its sound in one file: the picture copied as it is, the sound made AAC, ending with the shorter.</summary>
    public static IReadOnlyList<string> Mux(string picture, string sound, string output) =>
        ["-i", picture, "-i", sound, "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "aac", "-shortest", "-y", output];

    /// <summary>
    /// The sound of the owner's film as a track exactly <paramref name="length"/> long, in the narration's form, so it
    /// takes a narration's place in the timeline. A film with no sound track (null) gives silence of that length.
    /// </summary>
    public static IReadOnlyList<string> FootageSound(string? video, TimeSpan length, string output) =>
    [
        .. video is null
            ? new[] { "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo" }
            : ["-i", video, "-map", "0:a:0", "-af", "aformat=sample_rates=48000:channel_layouts=stereo,apad"], // padded: a film's sound can stop before its picture
        "-vn", "-t", Seconds(length), "-c:a", "pcm_s16le", "-y", output,
    ];

    /// <summary>
    /// The narration laid over the sound of the owner's film it is spoken across: the voice evened out and on top (about
    /// 21 dB above the film while it speaks, measured on a real clip; it was 15), the film's sound underneath at half its
    /// level and lower still while the voice speaks, so a laugh or a squeak still comes through between lines. As long as the voice, and in its form, so it takes the narration's
    /// place in the timeline unchanged.
    /// </summary>
    public static IReadOnlyList<string> VoiceOverFilm(string voice, string video, string output) =>
    [
        "-i", voice, "-i", video,
        "-filter_complex",
        // The voice is compressed a little and lifted, so its quiet words stay as clear as its loud ones.
        "[0:a]aformat=sample_rates=48000:channel_layouts=stereo,acompressor=threshold=0.1:ratio=3:attack=5:release=150:makeup=2,asplit=2[voice][key];" +
        "[1:a:0]aformat=sample_rates=48000:channel_layouts=stereo,apad,volume=0.5[film];" + // padded: a film's sound can stop before the voice
        "[film][key]sidechaincompress=threshold=0.04:ratio=4:attack=20:release=400[under];" +
        "[voice][under]amix=inputs=2:duration=first:normalize=0[out]",
        "-map", "[out]", "-c:a", "pcm_s16le", "-y", output,
    ];

    /// <summary>
    /// The outro's sound:a music track repeated to fill the outro exactly, in the narration's form so it takes the
    /// outro's slot like a clip's voice, coming in softly and fading out over its last two seconds. Turned well down:
    /// at full level it played 8 dB over the voice, and the levelling of the whole video takes back about half of any cut,
    /// so 16 dB brings it to about the voice's own level.
    /// </summary>
    public static IReadOnlyList<string> OutroMusic(string music, TimeSpan length, string output) =>
    [
        "-stream_loop", "-1", "-i", music, "-map", "0:a:0",
        "-af", $"aformat=sample_rates=48000:channel_layouts=stereo,volume=-16dB,afade=t=in:d=0.5,afade=t=out:st={Seconds(length - TimeSpan.FromSeconds(2))}:d=2",
        "-vn", "-t", Seconds(length), "-c:a", "pcm_s16le", "-y", output,
    ];

    /// <summary>A film's sound as speech to text reads it best: one channel at 16 kHz.</summary>
    public static IReadOnlyList<string> SpeechSound(string video, string output) =>
        ["-i", video, "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-y", output];

    /// <summary>
    /// One frame of a video at <paramref name="at"/>, <paramref name="width"/> pixels wide, or as large as the video
    /// for 0. Sought before the input is opened, so it is quick at any point.
    /// </summary>
    public static IReadOnlyList<string> Still(string video, TimeSpan at, int width, string output) =>
        ["-ss", Seconds(at), "-i", video, "-frames:v", "1", .. width > 0 ? new[] { "-vf", $"scale={width}:-2" } : [], "-y", output];

    /// <summary>The text of the list file <see cref="Join"/> reads: one finished clip per line, in running order.</summary>
    public static string ClipList(IEnumerable<string> clips) =>
        string.Concat(clips.Select(path => $"file '{path.Replace("'", @"'\''")}'\n"));

    /// <summary>
    /// Joins the finished clips by copying their pictures, and lays the crossfaded, levelled narration under them. No
    /// music plays under the clips: the series' music is the outro's own sound (see <see cref="OutroMusic"/>).
    /// </summary>
    /// <param name="whoosh">A sound (see <see cref="Synth.Whoosh"/>) played into each of the <paramref name="cuts"/>, loudest on the cut; null for none.</param>
    public static IReadOnlyList<string> Join(string clipListFile, IReadOnlyList<string> audios, string output, string? whoosh = null,
        IReadOnlyList<TimeSpan>? cuts = null)
    {
        var starts = whoosh is null ? [] : (cuts ?? []).Select(cut => cut - Synth.WhooshPeak).Where(start => start >= TimeSpan.Zero).ToList();
        var graph = new List<string>();
        for (var i = 0; i < audios.Count; i++)
            graph.Add($"[{i + 1}:a]aformat=sample_rates=48000:channel_layouts=stereo[s{i}]");
        var sound = "[s0]";
        for (var i = 1; i < audios.Count; i++)
        {
            graph.Add($"{sound}[s{i}]acrossfade=d={Seconds(AudioCrossfade)}[a{i}]");
            sound = $"[a{i}]";
        }
        if (starts.Count > 0)
        {
            // One copy of the whoosh for each cut, each held back to its moment and laid over the voice.
            var input = audios.Count + 1;
            // Well under the voice: a touch that marks the cut, not a noise over it.
            graph.Add($"[{input}:a]aformat=sample_rates=48000:channel_layouts=stereo,volume=-10dB,asplit={starts.Count}{string.Concat(starts.Select((_, j) => $"[w{j}]"))}");
            for (var j = 0; j < starts.Count; j++)
                graph.Add(FormattableString.Invariant($"[w{j}]adelay=delays={(long)starts[j].TotalMilliseconds}:all=1[d{j}]"));
            graph.Add($"{sound}{string.Concat(starts.Select((_, j) => $"[d{j}]"))}amix=inputs={starts.Count + 1}:duration=first:normalize=0[swept]");
            sound = "[swept]";
        }
        graph.Add($"{sound}loudnorm=I={TargetLufs.ToString(CultureInfo.InvariantCulture)}:TP=-1.5:LRA=11,aresample=48000[aout]");

        return
        [
            "-f", "concat", "-safe", "0", "-i", clipListFile,
            .. audios.SelectMany(path => new[] { "-i", path }),
            .. starts.Count == 0 ? [] : new[] { "-i", whoosh! },
            "-filter_complex", string.Join(';', graph),
            "-map", "0:v", "-map", "[aout]",
            "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-y", output,
        ];
    }
}
