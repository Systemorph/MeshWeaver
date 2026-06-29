namespace MeshWeaver.GitSync;

/// <summary>
/// Configuration for the per-user GitHub OAuth <b>authorization-code</b> flow, bound
/// from the <c>GitHub:OAuth</c> configuration section. A registered GitHub OAuth App
/// supplies <see cref="ClientId"/> + <see cref="ClientSecret"/>; its Authorization
/// callback URL must be <c>{portal}/connect/github/callback</c>. The flow redirects
/// the user to GitHub, GitHub redirects back to the callback with a <c>code</c>, and
/// the callback exchanges it for a long-standing access token which is stored
/// encrypted per-user (mirrors the LinkedIn connect).
/// </summary>
public sealed record GitHubOAuthOptions
{
    /// <summary>The GitHub OAuth App client id. Empty disables the feature (no Connect).</summary>
    public string? ClientId { get; init; }

    /// <summary>The GitHub OAuth App client secret (used only server-side in the code exchange).</summary>
    public string? ClientSecret { get; init; }

    /// <summary>OAuth scopes to request. <c>repo</c> grants read/write to private + public repos.</summary>
    public string Scopes { get; init; } = "repo";

    /// <summary>GitHub's authorize endpoint the browser is redirected to (start of the flow).</summary>
    public string AuthorizeUrl { get; init; } = "https://github.com/login/oauth/authorize";
    /// <summary>GitHub's token endpoint where the callback <c>code</c> is exchanged for an access token.</summary>
    public string TokenUrl { get; init; } = "https://github.com/login/oauth/access_token";
    /// <summary>GitHub's authenticated-user API endpoint, used to resolve the connected login.</summary>
    public string UserApiUrl { get; init; } = "https://api.github.com/user";

    /// <summary>True when both client id and secret are configured and the flow can run.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
