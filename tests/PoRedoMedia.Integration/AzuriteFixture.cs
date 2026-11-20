using System.Diagnostics;
using Testcontainers.Azurite;

namespace PoRedoMedia.IntegrationTests;

/// <summary>
/// One throwaway Azurite for the storage tests. Never the developer's compose instance. The container is only built
/// when Docker answers: building it without Docker throws, which would fail every test in the collection instead of
/// skipping the ones that need it.
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    private readonly AzuriteContainer? _container = DockerFactAttribute.DockerAvailable
        ? new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.37.0")
            .WithCommand("--skipApiVersionCheck")
            .Build()
        : null;

    /// <summary>Azurite's connection string, or a placeholder no test reaches when Docker is not running.</summary>
    public string ConnectionString => _container?.GetConnectionString() ?? "UseDevelopmentStorage=true";

    public Task InitializeAsync() => _container?.StartAsync() ?? Task.CompletedTask;

    public Task DisposeAsync() => _container?.DisposeAsync().AsTask() ?? Task.CompletedTask;
}

[CollectionDefinition(Name)]
public sealed class AzuriteCollection : ICollectionFixture<AzuriteFixture>
{
    public const string Name = "Azurite";
}

/// <summary>A fact that needs Docker. Without it the test is reported as skipped, never as passed.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public static bool DockerAvailable { get; } = Probe();

    public DockerFactAttribute()
    {
        if (!DockerAvailable)
            Skip = NoDocker;
    }

    /// <summary>The skip message: the Integration tier needs Docker (Testcontainers) for its throwaway Azurite.</summary>
    public const string NoDocker = "Skipped: Docker is not running. Start Docker Desktop to run the Integration tier (Testcontainers Azurite).";

    private static bool Probe()
    {
        try
        {
            using var docker = Process.Start(new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            return docker.WaitForExit(15_000) && docker.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>A theory that needs Docker.</summary>
public sealed class DockerTheoryAttribute : TheoryAttribute
{
    public DockerTheoryAttribute()
    {
        if (!DockerFactAttribute.DockerAvailable)
            Skip = DockerFactAttribute.NoDocker;
    }
}

/// <summary>A fact that needs Docker (Azurite) and ffmpeg on PATH, such as anything that renders or probes a video.</summary>
public sealed class DockerFFmpegFactAttribute : FactAttribute
{
    public DockerFFmpegFactAttribute()
    {
        if (!DockerFactAttribute.DockerAvailable)
            Skip = DockerFactAttribute.NoDocker;
        else if (!FFmpegFactAttribute.Available)
            Skip = FFmpegFactAttribute.NoFFmpeg;
    }
}
