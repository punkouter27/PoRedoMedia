using System.Security.Claims;

namespace PoRedoMedia.Client.Layout;

/// <summary>Shared account display helpers for the shell header and Settings page.</summary>
public static class AccountMenu
{
    /// <summary>Single uppercase avatar initial for a signed-in user name or email.</summary>
    public static string Initial(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : char.ToUpperInvariant(name.Trim()[0]).ToString();

    /// <summary>
    /// The signed-in account's email, which the header shows (never the display name): Microsoft sign-in's email or
    /// preferred_username claim (the server sends every claim, Program.cs), else the name when it is an address.
    /// </summary>
    public static string Email(ClaimsPrincipal user) =>
        user.Claims.FirstOrDefault(c => c.Type is ClaimTypes.Email or "email" or "preferred_username" && c.Value.Contains('@'))?.Value
        ?? user.Identity?.Name
        ?? "";
}
