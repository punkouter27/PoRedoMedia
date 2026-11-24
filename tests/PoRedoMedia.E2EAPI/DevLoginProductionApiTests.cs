using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PoRedoMedia.Api.Features.Auth;

namespace PoRedoMedia.E2EAPI;

/// <summary>
/// <c>/dev-login</c> is the test sign-in (Development and Test only). In any other environment the route does not
/// exist, so it answers 404 and nobody can sign in without Microsoft.
/// </summary>
public sealed class DevLoginProductionApiTests
{
    [Theory]
    [InlineData("Production", HttpStatusCode.NotFound)]
    [InlineData("Staging", HttpStatusCode.NotFound)]
    [InlineData("Test", HttpStatusCode.Redirect)]
    [InlineData("Development", HttpStatusCode.Redirect)]
    public async Task Dev_login_exists_only_in_Development_and_Test(string environment, HttpStatusCode expected)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication().AddCookie();
        builder.Services.AddAntiforgery();
        await using var app = builder.Build();
        app.MapAuthEndpoints();
        await app.StartAsync();

        using var client = app.GetTestClient();
        var response = await client.GetAsync("/dev-login?email=dev%40example.com&returnUrl=%2Fgallery");

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Redirect)
            Assert.Equal("/gallery", response.Headers.Location?.OriginalString);
    }
}
