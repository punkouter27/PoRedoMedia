namespace PoRedoMedia.Movie.Library;

/// <summary>
/// Which models the script is written with, and what they cost. The connections themselves (addresses, keys) belong to
/// the host, which hands the engine a delegate for each service; nothing here is a secret.
/// </summary>
/// <param name="ChatDeployment">The model that plans and writes a script.</param>
/// <param name="FastChatDeployment">The model for the small jobs: checking, watching a film's frames, upload notes.</param>
public sealed record AppSettings(string ChatDeployment, string FastChatDeployment)
{
    /// <summary>
    /// A model on the same machine for the small jobs. Never set on a server; kept so the writer's choice between it
    /// and the fast model reads as it always did.
    /// </summary>
    public string? LocalModel { get; init; }

    // What script and voice cost depends on the owner's own agreement, so the engine carries no price for them. Each is
    // null until given, and that service's use is then shown as an amount used, without a price.

    /// <summary>Rates of the main script model, as "input,cached input,output" in dollars a million tokens.</summary>
    public TokenRates? ChatRates { get; init; }

    /// <summary>The same for the fast model.</summary>
    public TokenRates? FastChatRates { get; init; }

    /// <summary>Dollars for a million characters spoken.</summary>
    public decimal? VoiceRate { get; init; }
}
