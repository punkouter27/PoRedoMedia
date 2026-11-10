namespace PoRedoMedia.Movie.Voice;

/// <summary>Writes down what is said in a recording, with when each word is said.</summary>
/// <param name="wavPath">Speech as a 16 kHz mono WAV file (<see cref="FfmpegArgs.SpeechSound"/>).</param>
public delegate Task<IReadOnlyList<WordTiming>> Hear(string wavPath, CancellationToken ct);