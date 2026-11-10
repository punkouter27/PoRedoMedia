using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Components.Forms;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Client.Services;

/// <summary>
/// Create Movie's calls. Whatever changes a movie answers with a <see cref="MovieReply"/>: the movie as it then is,
/// and what to tell the user. A refusal comes back as the reply's error, never as an exception.
/// </summary>
public sealed class MovieApi(HttpClient http)
{
    /// <summary>The largest picture or sound file the server takes.</summary>
    public const long MaxPicture = 10L * 1024 * 1024, MaxSound = 25L * 1024 * 1024;

    private Task<MovieConfigDto?>? _config;

    /// <summary>What this server offers; asked once.</summary>
    public Task<MovieConfigDto?> ConfigAsync() => _config ??= http.GetFromJsonAsync("api/movies/config", WireJson.Default.MovieConfigDto);

    public async Task<List<MovieSummaryDto>> LibraryAsync() => await http.GetFromJsonAsync("api/movies", WireJson.Default.ListMovieSummaryDto) ?? [];

    public Task<MoviePrefsDto?> PrefsAsync() => http.GetFromJsonAsync("api/movies/prefs", WireJson.Default.MoviePrefsDto);

    public async Task<MoviePrefsDto?> SavePrefsAsync(MoviePrefsDto prefs)
    {
        using var response = await http.PutAsJsonAsync("api/movies/prefs", prefs, WireJson.Default.MoviePrefsDto);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(WireJson.Default.MoviePrefsDto) : null;
    }

    /// <summary>The job under way, or the one that last ended; null when there has been none.</summary>
    public async Task<MovieJobDto?> JobAsync()
    {
        using var response = await http.GetAsync("api/movies/job");
        return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync(WireJson.Default.MovieJobDto) : null;
    }

    public async Task CancelJobAsync()
    {
        using var response = await http.DeleteAsync("api/movies/job");
    }

    // ---- The brand kit ----

    public Task<MovieKitDto?> KitAsync(string show) => http.GetFromJsonAsync($"api/movies/kit/{show}", WireJson.Default.MovieKitDto);

    public async Task<(MovieKitDto? Kit, string? Error)> SaveKitAsync(string show, MovieKitUpdate update)
    {
        using var response = await http.PutAsJsonAsync($"api/movies/kit/{show}", update, WireJson.Default.MovieKitUpdate);
        return await KitOrReasonAsync(response);
    }

    public async Task<(MovieKitDto? Kit, string? Error)> SetKitFileAsync(string show, string role, IBrowserFile file)
    {
        using var content = new StreamContent(file.OpenReadStream(role is "logo" or "mascot" or "host" ? MaxPicture : MaxSound));
        using var response = await http.PostAsync(role == "host"
            ? $"api/movies/kit/{show}/host/upload?name={Uri.EscapeDataString(file.Name)}"
            : $"api/movies/kit/{show}/files/{role}?name={Uri.EscapeDataString(file.Name)}", content);
        return await KitOrReasonAsync(response);
    }

    public async Task<(MovieKitDto? Kit, string? Error)> ClearKitFileAsync(string show, string role)
    {
        using var response = await http.DeleteAsync($"api/movies/kit/{show}/files/{role}");
        return await KitOrReasonAsync(response);
    }

    /// <summary>Uses a candidate design: as the logo, as the host, or the host as the mascot.</summary>
    /// <param name="what">"logo?token=…", "host?token=…" or "host-as-mascot".</param>
    public async Task<(MovieKitDto? Kit, string? Error)> KitUseAsync(string show, string what)
    {
        using var response = await http.PostAsync($"api/movies/kit/{show}/{what}", null);
        return await KitOrReasonAsync(response);
    }

    private static async Task<(MovieKitDto? Kit, string? Error)> KitOrReasonAsync(HttpResponseMessage response) =>
        response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync(WireJson.Default.MovieKitDto), null) : (null, await MediaApi.ReasonAsync(response));

    public Task<MovieReply> KitStillAsync(string show, bool outro, int number) => GetAsync($"api/movies/kit/{show}/still?outro={Flag(outro)}&number={number}");

    public Task<MovieReply> KitVideoAsync(string show, bool outro, int number) => PostAsync($"api/movies/kit/{show}/video?outro={Flag(outro)}&number={number}");

    public Task<MovieReply> KitSoundAsync(string show, bool outro) => PostAsync($"api/movies/kit/{show}/sound?outro={Flag(outro)}");

    public Task<MovieEstimateDto?> CandidatesEstimateAsync(string show, bool logos) =>
        http.GetFromJsonAsync($"api/movies/kit/{show}/candidates/estimate?logos={Flag(logos)}", WireJson.Default.MovieEstimateDto);

    public async Task<(List<string> Urls, string? Error)> CandidatesAsync(string show, bool logos)
    {
        using var response = await http.PostAsync($"api/movies/kit/{show}/candidates?logos={Flag(logos)}", null);
        return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync(WireJson.Default.ListString) ?? [], null) : ([], await MediaApi.ReasonAsync(response));
    }

    // ---- Starting a movie ----

    public async Task<MovieWatchReply> WatchFilmAsync(string show, MovieFilmDto film)
    {
        using var response = await http.PostAsJsonAsync($"api/movies/films/watch?show={show}", film, WireJson.Default.MovieFilmDto);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync(WireJson.Default.MovieWatchReply) ?? new(null, null, "Nothing came back.")
            : new(null, null, await MediaApi.ReasonAsync(response));
    }

    /// <summary>Whether what is said in one of the user's Library films is known already, so it need not be heard again.</summary>
    public async Task<bool> HeardAsync(Guid mediaId)
    {
        using var response = await http.GetAsync($"api/movies/films/{mediaId}/heard");
        return response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Trim() == "true";
    }

    public Task<MovieReply> StartAsync(MovieLessonRequest request, bool whole) => PostAsync(whole ? "api/movies/make" : "api/movies/write", request, WireJson.Default.MovieLessonRequest);

    // ---- One movie ----

    /// <summary>The movie; a reply with no movie and the error "damaged" when its file cannot be read; null when there is none.</summary>
    public async Task<MovieReply?> OpenAsync(string id)
    {
        using var response = await http.GetAsync($"api/movies/{id}");
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(WireJson.Default.MovieReply) : null;
    }

    public async Task<string?> DeleteAsync(string id)
    {
        using var response = await http.DeleteAsync($"api/movies/{id}");
        return response.IsSuccessStatusCode ? null : await MediaApi.ReasonAsync(response);
    }

    public Task<MovieReply> EditAsync(string id, MovieEdit edit) => PostAsync($"api/movies/{id}/edits", edit, WireJson.Default.MovieEdit);

    public Task<MovieReply> DialogueAsync(string id, Guid clip, string words) =>
        PostAsync($"api/movies/{id}/clips/{clip}/dialogue", new MovieEdit("dialogue", Text: words), WireJson.Default.MovieEdit);

    public Task<MovieReply> TeasersAsync(string id, MovieTeaserDto[] teasers) => SendAsync(HttpMethod.Put, $"api/movies/{id}/teasers", new MovieTeasersUpdate(teasers), WireJson.Default.MovieTeasersUpdate);

    public Task<MovieReply> UploadAsync(string id, MovieUploadRequest request) => PostAsync($"api/movies/{id}/youtube", request, WireJson.Default.MovieUploadRequest);

    public async Task<MovieReply> MusicAsync(string id, IBrowserFile file)
    {
        using var content = new StreamContent(file.OpenReadStream(MaxSound));
        using var response = await http.PostAsync($"api/movies/{id}/music?name={Uri.EscapeDataString(file.Name)}", content);
        return await ReplyAsync(response);
    }

    public Task<MovieEstimateDto?> EstimateAsync(string id, string what) => http.GetFromJsonAsync($"api/movies/{id}/{what}", WireJson.Default.MovieEstimateDto);

    /// <summary>Anything about a movie that needs no body: "undo", "voices", "clips/{id}/picture?another=true", and so on.</summary>
    public Task<MovieReply> DoAsync(string id, string action) => PostAsync($"api/movies/{id}/{action}");

    // ---- Plumbing ----

    private static string Flag(bool on) => on ? "true" : "false";

    private async Task<MovieReply> GetAsync(string path)
    {
        try
        {
            using var response = await http.GetAsync(path);
            return await ReplyAsync(response);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return Unreachable;
        }
    }

    private async Task<MovieReply> PostAsync(string path)
    {
        try
        {
            using var response = await http.PostAsync(path, null);
            return await ReplyAsync(response);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return Unreachable;
        }
    }

    private Task<MovieReply> PostAsync<T>(string path, T body, JsonTypeInfo<T> type) => SendAsync(HttpMethod.Post, path, body, type);

    private async Task<MovieReply> SendAsync<T>(HttpMethod method, string path, T body, JsonTypeInfo<T> type)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, type) };
            using var response = await http.SendAsync(request);
            return await ReplyAsync(response);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return Unreachable;
        }
    }

    private static readonly MovieReply Unreachable = new(null, Error: "The server could not be reached. Check your connection and try again.");

    private static async Task<MovieReply> ReplyAsync(HttpResponseMessage response) =>
        response.IsSuccessStatusCode
            ? response.StatusCode == HttpStatusCode.NoContent ? new MovieReply(null) : await response.Content.ReadFromJsonAsync(WireJson.Default.MovieReply) ?? new MovieReply(null)
            : new MovieReply(null, Error: response.StatusCode == HttpStatusCode.NotFound ? "That movie is no longer there." : await MediaApi.ReasonAsync(response));
}
