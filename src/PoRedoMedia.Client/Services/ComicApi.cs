using System.Net;
using System.Net.Http.Json;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Client.Services;

/// <summary>Comic-ify's calls. A refusal comes back as the reason, never as an exception.</summary>
public sealed class ComicApi(HttpClient http)
{
    public Task<ComicConfigDto?> ConfigAsync() => http.GetFromJsonAsync("api/comics/config", WireJson.Default.ComicConfigDto);

    /// <summary>The comic under way, or the one that last ended; null when there has been none since the server started.</summary>
    public async Task<ComicJobDto?> JobAsync()
    {
        using var response = await http.GetAsync("api/comics/job");
        return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync(WireJson.Default.ComicJobDto) : null;
    }

    public async Task<(ComicJobDto? Job, string? Error)> StartAsync(string conversation)
    {
        using var response = await http.PostAsJsonAsync("api/comics", new ComicRequest(conversation), WireJson.Default.ComicRequest);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync(WireJson.Default.ComicJobDto), null)
            : (null, await MediaApi.ReasonAsync(response));
    }
}
