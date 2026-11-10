using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PoRedoMedia.Movie.Render;

public sealed class FfmpegException(string message) : Exception(message);

/// <summary>The only place that starts FFmpeg processes.</summary>
public sealed partial class FfmpegRunner(string ffmpegPath)
{
    private const int LogLinesKept = 20;

    /// <summary>A program's file name on this system: "ffmpeg.exe" on Windows, "ffmpeg" elsewhere.</summary>
    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>Finds FFmpeg in <paramref name="folder"/> when one is given and has it, else on PATH; null when it is not installed.</summary>
    public static string? Locate(string? folder = null) =>
        folder is { Length: > 0 } && File.Exists(Path.Combine(folder, Exe("ffmpeg"))) ? Path.GetFullPath(Path.Combine(folder, Exe("ffmpeg")))
        : LocateIn(Environment.GetEnvironmentVariable("PATH") ?? "");

    // Relative PATH entries resolve against whatever folder the app was started from, so a stray ffmpeg there
    // would be run in place of the real one. Only absolute folders are searched.
    public static string? LocateIn(string pathVariable) =>
        pathVariable
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => dir.Trim())
            .Where(Path.IsPathFullyQualified)
            .Select(dir => Path.Combine(dir, Exe("ffmpeg")))
            .FirstOrDefault(File.Exists);

    /// <param name="total">Expected output length; with <paramref name="progress"/> it turns FFmpeg's clock into 0..1.</param>
    /// <param name="feed">Writes what FFmpeg reads as <c>pipe:0</c>, such as frames drawn here; null when it reads only files.</param>
    public async Task RunAsync(IReadOnlyList<string> args, string workingDirectory, TimeSpan? total, IProgress<double>? progress, CancellationToken ct,
        Func<Stream, CancellationToken, Task>? feed = null)
    {
        var (exitCode, _, log) = await StartAsync(
            ffmpegPath, ["-hide_banner", .. feed is null ? new[] { "-nostdin" } : [], "-nostats", "-progress", "pipe:1", .. FilesOnly(args)], workingDirectory,
            line =>
            {
                if (total is { Ticks: > 0 } && progress is not null && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(12), out var microseconds))
                    progress.Report(Math.Clamp(microseconds / 1e6 / total.Value.TotalSeconds, 0, 1));
            }, ct, feed: feed);
        if (exitCode != 0)
            throw new FfmpegException($"FFmpeg stopped with an error.{Environment.NewLine}{log}");
    }

    public async Task<string> ProbeAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpegPath)!, Exe("ffprobe"));
        var (exitCode, output, log) = await StartAsync(ffprobe, ["-protocol_whitelist", "file", .. args], Environment.CurrentDirectory, null, ct);
        return exitCode == 0 ? output : throw new FfmpegException($"The file could not be read.{Environment.NewLine}{log}");
    }

    /// <summary>Length of a video or audio file.</summary>
    /// <exception cref="FfmpegException">The file is not readable media.</exception>
    public async Task<TimeSpan> ProbeDurationAsync(string path, CancellationToken ct)
    {
        var output = await ProbeAsync(["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path], ct);
        return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : throw new FfmpegException("The file's length could not be read.");
    }

    /// <summary>Integrated loudness of a file's audio, in LUFS. Only the end-to-end test measures this, so it is not public.</summary>
    internal async Task<double> MeasureLoudnessAsync(string path, CancellationToken ct)
    {
        var (_, _, log) = await StartAsync(
            ffmpegPath, ["-hide_banner", "-nostdin", "-nostats", .. FilesOnly(["-i", path]), "-af", "ebur128", "-f", "null", "-"],
            Environment.CurrentDirectory, null, ct, keepLogLines: 40);
        var match = IntegratedLoudness().Matches(log).LastOrDefault()
            ?? throw new FfmpegException($"Loudness could not be measured.{Environment.NewLine}{log}");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Every input may be read from a file, or from the app's own pipe, and from nowhere else. The files come from the
    /// episode folder, which is often cloud-synced and so not trusted: a playlist named like a film would otherwise
    /// have FFmpeg fetch whatever it lists from the network.
    /// </summary>
    private static IEnumerable<string> FilesOnly(IEnumerable<string> args) =>
        args.SelectMany(arg => arg == "-i" ? ["-protocol_whitelist", "file,pipe", arg] : new[] { arg });

    private static async Task<(int ExitCode, string Output, string Log)> StartAsync(
        string exe, IReadOnlyList<string> args, string workingDirectory, Action<string>? onOutputLine, CancellationToken ct, int keepLogLines = LogLinesKept,
        Func<Stream, CancellationToken, Task>? feed = null)
    {
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = feed is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = info };
        var output = new System.Text.StringBuilder();
        var log = new Queue<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (onOutputLine is null) lock (output) output.AppendLine(e.Data);
            else onOutputLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (log)
            {
                log.Enqueue(e.Data);
                if (log.Count > keepLogLines) log.Dequeue();
            }
        };

        ct.ThrowIfCancellationRequested();
        process.Start();
        // Stopped the moment the job is cancelled, on the cancelling thread: when the app is closing there is no
        // later moment, and FFmpeg left alone would carry on encoding with nobody to hand the result to.
        using var stop = ct.Register(() => Kill(process));
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (feed is not null)
        {
            try
            {
                await using var input = process.StandardInput.BaseStream;
                await feed(input, ct);
            }
            catch (IOException)
            {
                // FFmpeg stopped reading: it has failed, and its log below says why.
            }
            catch (OperationCanceledException)
            {
                // Cancelled while feeding: the process is being killed above, and the wait below sees it go.
            }
        }
        await process.WaitForExitAsync(CancellationToken.None); // files stay locked until it is really gone
        ct.ThrowIfCancellationRequested();
        return (process.ExitCode, output.ToString(), string.Join(Environment.NewLine, log));
    }

    /// <summary>Ends a program the app started, and everything it started in turn. Safe when it has already gone.</summary>
    internal static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already finished, or finishing: there is nothing left to stop.
        }
    }

    [GeneratedRegex(@"I:\s+(-?[\d.]+) LUFS")]
    private static partial Regex IntegratedLoudness();
}

/// <summary>Reports on the calling thread; <see cref="Progress{T}"/> would post to a context and reorder values.</summary>
internal sealed class Relay<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
