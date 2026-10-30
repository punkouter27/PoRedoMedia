using System.Security.Claims;
using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http.HttpResults;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Preferences;

/// <summary>One row per user (partition = user, one fixed row key) holding the preferences as JSON.</summary>
public sealed class PreferencesRepository(StorageClients storage)
{
    private const string Row = "preferences";
    private readonly Lazy<TableClient> _table = new(() => storage.Table(StorageNames.Tables.Preferences));

    /// <summary>The user's preferences; empty when none were ever saved.</summary>
    public async Task<Dictionary<string, string>> GetAsync(UserId owner, CancellationToken ct = default)
    {
        var row = await _table.Value.GetEntityIfExistsAsync<TableEntity>(owner.Key, Row, cancellationToken: ct);
        return row.HasValue && row.Value!.GetString("Values") is { } json
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []
            : [];
    }

    public Task SaveAsync(UserId owner, Dictionary<string, string> values, CancellationToken ct = default) =>
        _table.Value.UpsertEntityAsync(
            new TableEntity(owner.Key, Row) { ["Values"] = JsonSerializer.Serialize(values) }, TableUpdateMode.Replace, ct);
}

public static class PreferencesEndpoints
{
    public static IEndpointRouteBuilder MapPreferences(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/preferences").RequireAntiforgeryValidation();
        group.MapGet("/", async (ClaimsPrincipal user, PreferencesRepository preferences, CancellationToken ct) =>
            TypedResults.Ok(new PreferencesDto(await preferences.GetAsync(UserId.From(user), ct))));
        group.MapPut("/", SaveAsync);
        return app;
    }

    /// <summary>Replaces the whole set: what the browser sends is what the account keeps.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> SaveAsync(
        PreferencesDto request, ClaimsPrincipal user, PreferencesRepository preferences, CancellationToken ct)
    {
        var values = request.Values ?? [];
        if (values.Any(v => string.IsNullOrWhiteSpace(v.Key) || v.Key.Length > PreferencesDto.MaxKeyLength || v.Value is null))
            return Problem($"Every preference needs a key of 1 to {PreferencesDto.MaxKeyLength} characters and a value.");
        if (JsonSerializer.SerializeToUtf8Bytes(values).Length > PreferencesDto.MaxBytes)
            return Problem($"Preferences are limited to {PreferencesDto.MaxBytes / 1024} KB.");

        await preferences.SaveAsync(UserId.From(user), values, ct);
        return TypedResults.NoContent();
    }

    private static ProblemHttpResult Problem(string detail) =>
        TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest);
}
