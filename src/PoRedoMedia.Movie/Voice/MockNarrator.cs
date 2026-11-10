namespace PoRedoMedia.Movie.Voice;

/// <summary>
/// Stand-in for when no voice service is configured: a soft tone as long as the words would take to say, so a movie
/// can still be timed, previewed and rendered end to end.
/// </summary>
public sealed class MockNarrator : INarrator
{
    private const int SampleRate = 48000;

    public async Task<Narration> SynthesizeAsync(string text, string outputPath, double rate, CancellationToken ct)
    {
        if (text.Trim().Length == 0)
            throw new InvalidOperationException("This clip has no words to say. Write some, or let it keep its own sound.");
        var duration = Durations.Estimate(text) / Math.Clamp(rate, 0.5, 2.0);
        Files.EnsureFolderFor(outputPath);
        await File.WriteAllBytesAsync(outputPath, Tone(duration), ct);
        // Timings are spread by word length, not measured. Real ones come from a voice service that is listened back to.
        return new Narration(outputPath, duration, WordTiming.Spread(text, duration));
    }

    /// <summary>A quiet hum with a short fade at each end, as 16-bit mono PCM in a WAV file.</summary>
    private static byte[] Tone(TimeSpan duration)
    {
        var samples = Math.Max(1, (int)(duration.TotalSeconds * SampleRate));
        var fade = Math.Min(samples / 2, SampleRate / 20);
        using var stream = new MemoryStream(44 + samples * 2);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);            // PCM
        writer.Write((short)1);            // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);      // bytes a second
        writer.Write((short)2);            // bytes a sample
        writer.Write((short)16);           // bits a sample
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
        {
            var edge = Math.Min(1.0, Math.Min(i, samples - 1 - i) / (double)Math.Max(1, fade));
            writer.Write((short)(Math.Sin(2 * Math.PI * 180 * i / SampleRate) * 1800 * edge));
        }
        writer.Flush();
        return stream.ToArray();
    }
}
