namespace PoRedoMedia.Shared.Models;

/// <summary>
/// A user's preferences as the account keeps them: string keys to string values, such as the
/// Settings switches (<c>po.adv.*</c>). The server stores them and never reads them.
/// </summary>
public sealed record PreferencesDto(Dictionary<string, string> Values)
{
    /// <summary>Most bytes the values may take, as UTF-8 JSON.</summary>
    public const int MaxBytes = 16 * 1024;

    public const int MaxKeyLength = 64;
}
