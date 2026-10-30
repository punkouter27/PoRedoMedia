using System.Net;
using System.Net.Http.Json;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.IntegrationTests;

[Collection(AzuriteCollection.Name)]
public sealed class PreferencesTests(AzuriteFixture azurite)
{
    [DockerFact]
    public async Task Preferences_follow_the_account_are_private_to_it_and_are_capped()
    {
        using var factory = new AppFactory(azurite.ConnectionString);
        var me = await factory.SignedInAsync($"dev|{Guid.NewGuid()}");
        var someoneElse = await factory.SignedInAsync($"dev|{Guid.NewGuid()}");

        Assert.Empty((await me.GetFromJsonAsync("/api/preferences", WireJson.Default.PreferencesDto))!.Values);

        var mine = new PreferencesDto(new() { ["po.adv.costs"] = "on", ["po.theme"] = "dark" });
        Assert.Equal(HttpStatusCode.NoContent, (await me.PutAsJsonAsync("/api/preferences", mine, WireJson.Default.PreferencesDto)).StatusCode);
        Assert.Equal(mine.Values, (await me.GetFromJsonAsync("/api/preferences", WireJson.Default.PreferencesDto))!.Values);
        Assert.Empty((await someoneElse.GetFromJsonAsync("/api/preferences", WireJson.Default.PreferencesDto))!.Values);

        // Over the cap is refused whole, and what was saved stays.
        var huge = new PreferencesDto(new() { ["big"] = new string('x', PreferencesDto.MaxBytes) });
        Assert.Equal(HttpStatusCode.BadRequest, (await me.PutAsJsonAsync("/api/preferences", huge, WireJson.Default.PreferencesDto)).StatusCode);
        Assert.Equal(mine.Values, (await me.GetFromJsonAsync("/api/preferences", WireJson.Default.PreferencesDto))!.Values);

        // A write without the antiforgery token is refused like every other JSON write.
        var tokenless = factory.CreateClient();
        tokenless.DefaultRequestHeaders.Add("X-Fake-User", "dev|tokenless");
        Assert.Equal(HttpStatusCode.BadRequest, (await tokenless.PutAsJsonAsync("/api/preferences", mine, WireJson.Default.PreferencesDto)).StatusCode);
    }
}
