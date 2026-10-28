using System.Net;

namespace PoRedoMedia.E2EAPI;

public sealed class HealthApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Live_probe_answers_200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_probe_is_anonymous_and_reports_unreachable_storage_as_503()
    {
        // The Test host has no storage account, so readiness must fail loudly rather than redirect to sign-in.
        var response = await factory.CreateNoRedirectClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
