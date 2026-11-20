using System.Net.Sockets;

namespace PoRedoMedia.E2EUI;

/// <summary>
/// A fact that needs the app running at <c>E2E_BASE_URL</c> (default http://localhost:4100) and Playwright's Chromium
/// installed. When either is missing the test is reported as skipped with a message saying which, never as passed.
/// Start everything with <c>scripts/run-e2e.ps1</c>.
/// </summary>
public sealed class LiveServerFactAttribute : FactAttribute
{
    public static string BaseUrl { get; } =
        (Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? "http://localhost:4100").TrimEnd('/');

    private static readonly Lazy<bool> Reachable = new(() =>
    {
        try
        {
            var uri = new Uri(BaseUrl);
            using var tcp = new TcpClient();
            return tcp.ConnectAsync(uri.Host, uri.Port).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            return false;
        }
    });

    private static readonly Lazy<bool> BrowserInstalled = new(() =>
    {
        var root = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") is { Length: > 0 } custom && custom != "0"
            ? custom
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright")
                : OperatingSystem.IsMacOS()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "ms-playwright")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "ms-playwright");
        return Directory.Exists(root) && Directory.EnumerateDirectories(root, "chromium*").Any();
    });

    private static readonly Lazy<bool> FFmpegOnPath = new(() =>
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            return p.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    });

    /// <summary>
    /// Set on tests that make or render video (the test or the app it drives runs ffmpeg): they skip with a message
    /// when ffmpeg is not on PATH, instead of failing.
    /// </summary>
    public bool NeedsFFmpeg
    {
        get => _needsFFmpeg;
        init
        {
            _needsFFmpeg = value;
            if (value && Skip is null && !FFmpegOnPath.Value)
                Skip = "Skipped: ffmpeg is not on PATH, and this test makes or renders video. Install ffmpeg to run it.";
        }
    }

    private readonly bool _needsFFmpeg;

    public LiveServerFactAttribute()
    {
        if (!Reachable.Value)
            Skip = $"Skipped: no app is running at {BaseUrl}. Start it (or run scripts/run-e2e.ps1), or set E2E_BASE_URL.";
        else if (!BrowserInstalled.Value)
            Skip = "Skipped: Playwright's Chromium is not installed. Run: pwsh tests/PoRedoMedia.E2EUI/bin/Release/net10.0/playwright.ps1 install chromium";
    }
}
