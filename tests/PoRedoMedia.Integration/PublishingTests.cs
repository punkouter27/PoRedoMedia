using System.Net;
using System.Net.Http.Json;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using PoRedoMedia.Api.Features.Publishing;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.IntegrationTests;

[Collection(AzuriteCollection.Name)]
public sealed class PublishingTests(AzuriteFixture azurite)
{
    [DockerFact]
    public async Task A_site_is_connected_by_its_own_sign_in_kept_encrypted_for_that_user_only_and_disconnected()
    {
        using var factory = new AppFactory(azurite.ConnectionString);
        var userId = $"dev|{Guid.NewGuid()}";
        var me = await factory.SignedInAsync(userId);
        var someoneElse = await factory.SignedInAsync($"dev|{Guid.NewGuid()}");

        // Mock AI stands in both sites: set up, nothing connected yet, and Publish refuses what is not connected.
        var status = await me.GetFromJsonAsync("/api/publish/status", WireJson.Default.PublishStatusDto);
        Assert.Equal((true, false, true, false), (status!.YouTube.Configured, status.YouTube.Connected, status.TikTok.Configured, status.TikTok.Connected));
        var refused = await me.PostAsJsonAsync("/api/movies/none/publish", new PublishRequest(true, "private", false, "draft", "SELF_ONLY"), WireJson.Default.PublishRequest);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Connect YouTube first.", await refused.Content.ReadAsStringAsync());

        // Connect goes to the site's sign-in with a state; coming back with it stores the connection.
        var connect = await me.GetAsync("/api/publish/youtube/connect");
        Assert.Equal(HttpStatusCode.Redirect, connect.StatusCode);
        var signIn = connect.Headers.Location!;
        Assert.Equal("/api/publish/youtube/callback", signIn.AbsolutePath);
        // Someone else cannot use this sign-in's state; and it works once.
        var stolen = await someoneElse.GetAsync(signIn.PathAndQuery);
        Assert.Contains("connectError", stolen.Headers.Location!.OriginalString, StringComparison.Ordinal);
        connect = await me.GetAsync("/api/publish/youtube/connect");
        var back = await me.GetAsync(connect.Headers.Location!.PathAndQuery);
        Assert.Equal("/movie?connected=YouTube", back.Headers.Location!.OriginalString);
        Assert.Contains("connectError", (await me.GetAsync(connect.Headers.Location!.PathAndQuery)).Headers.Location!.OriginalString, StringComparison.Ordinal);

        Assert.True((await me.GetFromJsonAsync("/api/publish/status", WireJson.Default.PublishStatusDto))!.YouTube.Connected);
        Assert.False((await someoneElse.GetFromJsonAsync("/api/publish/status", WireJson.Default.PublishStatusDto))!.YouTube.Connected);

        // Stored encrypted: the table never holds the refresh token as it is.
        var table = new TableClient(azurite.ConnectionString, "PlatformTokens");
        var rows = table.QueryAsync<TableEntity>(e => e.RowKey == "YouTube");
        var found = false;
        await foreach (var row in rows)
        {
            Assert.DoesNotContain("fake-google-refresh", row.GetString("Token"), StringComparison.Ordinal);
            found = true;
        }
        Assert.True(found);

        // Disconnect revokes it at the site and forgets it here; it is a write, so it needs the antiforgery token.
        var tokenless = factory.CreateClient();
        tokenless.DefaultRequestHeaders.Add("X-Fake-User", userId);
        Assert.Equal(HttpStatusCode.BadRequest, (await tokenless.PostAsync("/api/publish/youtube/disconnect", null)).StatusCode);
        var after = await (await me.PostAsync("/api/publish/youtube/disconnect", null)).Content.ReadFromJsonAsync(WireJson.Default.PublishStatusDto);
        Assert.False(after!.YouTube.Connected);
        Assert.Contains("fake-google-refresh-fake-google-code", ((FakeYouTubePublisher)factory.Services.GetRequiredService<IYouTubePublisher>()).Revoked);
    }
}
