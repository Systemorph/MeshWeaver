namespace MeshWeaver.GitSync;

/// <summary>
/// GitHub <b>App</b> (machine identity) configuration, bound from the <c>GitHub:App</c>
/// configuration section. Where <see cref="GitHubOAuthOptions"/> authenticates a <i>user</i>
/// (per-user connect flow), these credentials authenticate the <i>platform itself</i>: GitSync
/// signs a short-lived JWT with the App's private key and exchanges it for an
/// <b>installation token</b> — so server-side operations (the plugin registry's sync of the
/// plugins repo, boot imports) never run on anyone's personal credentials.
/// </summary>
public sealed record GitHubAppOptions
{
    /// <summary>The App's client id (<c>Iv23li…</c>; the numeric App ID also works as the JWT issuer).</summary>
    public string? ClientId { get; init; }

    /// <summary>The App's private key, PEM text (Settings → GitHub Apps → Private keys). Ship via KeyVault/env.</summary>
    public string? PrivateKey { get; init; }

    /// <summary>
    /// The installation to mint tokens for. Optional — when unset the service discovers the App's
    /// installations and picks the single one (or the one matching <see cref="InstallationOwner"/>).
    /// </summary>
    public long? InstallationId { get; init; }

    /// <summary>Account login to select among multiple installations (e.g. <c>Systemorph</c>).</summary>
    public string? InstallationOwner { get; init; }

    /// <summary>GitHub REST API base URL (override for GHES).</summary>
    public string ApiBaseUrl { get; init; } = "https://api.github.com";

    /// <summary>True when the App identity is usable (client id + private key present).</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(PrivateKey);
}
