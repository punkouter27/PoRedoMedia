using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PoRedoMedia.Api.Common.Health;

namespace PoRedoMedia.UnitTests;

/// <summary>
/// /health and /api/diag read one report, checked at most once a minute (Common/Health/HealthReportCache), so a
/// burst of page loads never calls the outside services more than once.
/// </summary>
public sealed class HealthReportCacheTests
{
    [Fact]
    public async Task Two_reads_within_a_minute_share_one_check_and_its_time()
    {
        var health = new CountingHealthService();
        var clock = new ManualClock();
        var cache = new HealthReportCache(health, clock);

        var first = await cache.GetAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(59));
        var second = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(1, health.Runs);
        Assert.Same(first, second);
        Assert.Equal(first.CheckedAt, second.CheckedAt);
    }

    [Fact]
    public async Task A_read_after_a_minute_checks_again()
    {
        var health = new CountingHealthService();
        var clock = new ManualClock();
        var cache = new HealthReportCache(health, clock);

        var first = await cache.GetAsync(CancellationToken.None);
        clock.Advance(HealthReportCache.CacheFor);
        var second = await cache.GetAsync(CancellationToken.None);

        Assert.Equal(2, health.Runs);
        Assert.True(second.CheckedAt > first.CheckedAt);
    }

    [Fact]
    public async Task Reads_at_the_same_moment_wait_for_one_check()
    {
        var health = new CountingHealthService { Delay = TimeSpan.FromMilliseconds(200) };
        var cache = new HealthReportCache(health, new ManualClock());

        var reports = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.GetAsync(CancellationToken.None)));

        Assert.Equal(1, health.Runs);
        Assert.All(reports, r => Assert.Same(reports[0], r));
    }

    [Fact]
    public async Task A_caller_that_gives_up_does_not_cancel_the_shared_check()
    {
        var health = new CountingHealthService { Delay = TimeSpan.FromMilliseconds(100) };
        var cache = new HealthReportCache(health, new ManualClock());
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();

        var report = await cache.GetAsync(gone.Token);

        Assert.Equal(HealthStatus.Healthy, report.Report.Status);
        Assert.False(health.SawCancelledToken);
    }

    [Fact]
    public void Every_check_has_a_ten_second_timeout()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPoHealthChecks();
        using var provider = services.BuildServiceProvider();

        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        Assert.NotEmpty(registrations);
        Assert.All(registrations, r => Assert.Equal(TimeSpan.FromSeconds(10), r.Timeout));
    }

    private sealed class CountingHealthService : HealthCheckService
    {
        private int _runs;

        public int Runs => _runs;

        public TimeSpan Delay { get; init; }

        public bool SawCancelledToken { get; private set; }

        public override async Task<HealthReport> CheckHealthAsync(Func<HealthCheckRegistration, bool>? predicate, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _runs);
            SawCancelledToken |= cancellationToken.IsCancellationRequested;
            await Task.Delay(Delay, CancellationToken.None);
            return new HealthReport(new Dictionary<string, HealthReportEntry>
            {
                ["server"] = new(HealthStatus.Healthy, "Answering.", TimeSpan.Zero, null, null),
            }, TimeSpan.Zero);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
