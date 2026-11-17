using Azure.Data.Tables;
using Microsoft.AspNetCore.DataProtection;

namespace PoRedoMedia.Api.Features.Publishing;

/// <summary>
/// Each user's connection to YouTube and TikTok: the long-lived refresh token, encrypted with ASP.NET Core Data
/// Protection before it is stored (the table never sees it in the clear), one row per user and site. Disconnecting
/// deletes the row.
/// </summary>
public sealed class PlatformTokenStore(StorageClients storage, IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("PoRedoMedia.Publishing.RefreshTokens.v1");
    private readonly Lazy<TableClient> _table = new(() => storage.Table(StorageNames.Tables.PlatformTokens));

    /// <summary>The user's refresh token for the site, or null when not connected (or it can no longer be read).</summary>
    public async Task<string?> GetAsync(UserId owner, Platform platform, CancellationToken ct = default)
    {
        var row = await _table.Value.GetEntityIfExistsAsync<TableEntity>(owner.Key, platform.ToString(), cancellationToken: ct);
        if (!row.HasValue || row.Value!.GetString("Token") is not { Length: > 0 } sealedToken)
            return null;
        try
        {
            return _protector.Unprotect(sealedToken);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null; // the keys it was sealed with are gone: connect again
        }
    }

    public Task SaveAsync(UserId owner, Platform platform, string refreshToken, CancellationToken ct = default) =>
        _table.Value.UpsertEntityAsync(new TableEntity(owner.Key, platform.ToString())
        {
            ["Token"] = _protector.Protect(refreshToken),
            ["Saved"] = DateTimeOffset.UtcNow,
        }, TableUpdateMode.Replace, ct);

    public Task DeleteAsync(UserId owner, Platform platform, CancellationToken ct = default) =>
        _table.Value.DeleteEntityAsync(owner.Key, platform.ToString(), cancellationToken: ct);
}
