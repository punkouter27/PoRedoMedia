namespace PoRedoMedia.Client.Layout;

/// <summary>Shared account display helpers for the shell header and Settings page.</summary>
public static class AccountMenu
{
    /// <summary>Single uppercase avatar initial for a signed-in user name or email.</summary>
    public static string Initial(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : char.ToUpperInvariant(name.Trim()[0]).ToString();
}
