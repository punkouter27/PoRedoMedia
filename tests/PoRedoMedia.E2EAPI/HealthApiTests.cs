using System.Net;
using System.Net.Http.Json;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.E2EAPI;

public sealed class HealthApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Live_probe_answers_200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_probe_is_anonymous_and_reports_unreachable_storage_as_503()
    {
        // The Test host has no storage account, so readiness must fail loudly rather than redirect to sign-in.
        var response = await factory.CreateNoRedirectClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Diag_shows_services_switched_off_on_purpose_as_Off_and_never_as_a_problem()
    {
        // Test has no Application Insights and uses stand-in AI, so those rows are "Off": not called, no problem text,
        // and they never make the overall status worse than the real checks do.
        var report = (await factory.CreateClient().GetFromJsonAsync("/api/diag", WireJson.Default.DiagReportDto))!;

        var off = report.Checks.Where(c => c.Status == "Off").ToList();
        Assert.Contains(off, c => c.Name == "app-insights");
        Assert.All(off, c => Assert.Null(c.Problem));
        Assert.All(report.Checks, c => Assert.True(c.Status is "Healthy" or "Degraded" or "Unhealthy" or "Off", c.Status));
        var worst = report.Checks.Where(c => c.Status != "Off").Select(c => c.Status)
            .OrderBy(s => s switch { "Unhealthy" => 0, "Degraded" => 1, _ => 2 }).First();
        Assert.Equal(worst, report.Status);
    }
}
