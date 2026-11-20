using System.Net;
using System.Net.Http.Json;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.IntegrationTests;

[Collection(AzuriteCollection.Name)]
public sealed class DiagAndRoutingTests(AzuriteFixture azurite)
{
    [DockerFact]
    public async Task Diag_reports_every_dependency_to_anyone_and_gives_nothing_away()
    {
        using var factory = new AppFactory(azurite.ConnectionString, settings: new()
        {
            ["KeyVault:Uri"] = "https://kv-secret-name.vault.azure.net/",
            ["AzureAd:TenantId"] = "11111111-2222-3333-4444-555555555555",
            ["Auth:AllowedEmails"] = "someone@example.com",
        });
        var anonymous = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await anonymous.GetAsync("/api/diag");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var report = (await response.Content.ReadFromJsonAsync(WireJson.Default.DiagReportDto))!;

        Assert.Equal("Healthy", report.Checks.Single(c => c.Name == "storage").Status);
        Assert.Equal("data", report.Checks.Single(c => c.Name == "storage").Group);
        Assert.Equal("api", report.Checks.Single(c => c.Name == "server").Group);
        Assert.Contains(report.Checks, c => c.Group == "service");
        Assert.All(report.Checks, c => Assert.True(c.Group is "api" or "data" or "service", c.Group));
        Assert.All(report.Checks, c => Assert.Equal(c.Status == "Healthy", c.Problem is null));
        Assert.All(report.Checks, c => Assert.False(string.IsNullOrWhiteSpace(c.Purpose)));
        Assert.Matches("^\\d{6}$|^unknown$", report.Version);

        // No connection string, key, address, vault, tenant, email, path or exception message.
        foreach (var secret in new[]
                 {
                     azurite.ConnectionString, "AccountKey", "DefaultEndpointsProtocol", "devstoreaccount1", "127.0.0.1", "localhost",
                     "kv-secret-name", "vault.azure.net", "11111111-2222", "@example.com", "http", ":\\\\", "/home/", "Exception:", "   at ",
                 })
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }

        // The probes the deploy's gate reads: live is the process, /health the dependencies (storage is up here).
        Assert.Equal("Healthy", await anonymous.GetStringAsync("/health/live"));
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
    }

    [DockerFact]
    public async Task An_unknown_page_is_the_apps_own_404_an_unknown_api_path_is_json_and_a_bad_form_is_a_validation_problem()
    {
        using var factory = new AppFactory(azurite.ConnectionString);
        var client = await factory.SignedInAsync($"dev|{Guid.NewGuid()}");

        // A browser asking for a page that does not exist gets the app (it renders "Page not found"), with a real 404.
        using var page = new HttpRequestMessage(HttpMethod.Get, "/zz-missing");
        page.Headers.Accept.ParseAdd("text/html");
        var pageResponse = await client.SendAsync(page);
        Assert.Equal(HttpStatusCode.NotFound, pageResponse.StatusCode);
        Assert.Equal("text/html", pageResponse.Content.Headers.ContentType?.MediaType);
        var html = await pageResponse.Content.ReadAsStringAsync();
        Assert.Contains("_framework/blazor.web.js", html, StringComparison.Ordinal);
        // The signed-in user goes along, so the shell around "Page not found" is the signed-in one.
        Assert.Contains("Blazor-WebAssembly-Component-State", html, StringComparison.Ordinal);

        // /diag is a page of the app, not a 404.
        using var diag = new HttpRequestMessage(HttpMethod.Get, "/diag");
        diag.Headers.Accept.ParseAdd("text/html");
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().SendAsync(diag)).StatusCode);

        // The API never answers with the page.
        using var api = new HttpRequestMessage(HttpMethod.Get, "/api/zz-missing");
        api.Headers.Accept.ParseAdd("text/html");
        var apiResponse = await client.SendAsync(api);
        Assert.Equal(HttpStatusCode.NotFound, apiResponse.StatusCode);
        Assert.Equal("application/problem+json", apiResponse.Content.Headers.ContentType?.MediaType);

        // A form's request that breaks its rules is a validation problem by field, with the message as detail too.
        var response = await client.PostAsJsonAsync("/api/comics", new ComicRequest("hi"), WireJson.Default.ComicRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Paste a conversation of at least a couple of lines.", problem.GetProperty("errors").GetProperty("Conversation")[0].GetString());
        Assert.Equal("Paste a conversation of at least a couple of lines.", problem.GetProperty("detail").GetString());
    }
}
