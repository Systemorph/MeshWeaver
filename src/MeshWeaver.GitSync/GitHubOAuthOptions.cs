namespace MeshWeaver.GitSync;

/// <summary>
/// Configuration for the per-user GitHub OAuth device flow, bound from the
/// <c>GitHub:OAuth</c> configuration section. A registered GitHub <b>OAuth App</b>
/// (device flow enabled) supplies the <see cref="ClientId"/>; classic OAuth App
/// device-flow tokens are long-standing (non-expiring), satisfying the
/// "long-standing OAuth credential" requirement. No client secret is needed for
/// the device authorization grant.
/// </summary>
public sealed record GitHubOAuthOptions
{
    /// <summary>The GitHub OAuth App client id. When empty the feature is disabled (no Connect).</summary>
    public string? ClientId { get; init; }

    /// <summary>OAuth scopes to request. <c>repo</c> grants read/write to private + public repos.</summary>
    public string Scopes { get; init; } = "repo";

    public string DeviceCodeUrl { get; init; } = "https://github.com/login/device/code";
    public string TokenUrl { get; init; } = "https://github.com/login/oauth/access_token";
    public string UserApiUrl { get; init; } = "https://api.github.com/user";

    /// <summary>True when a client id is configured and the Connect flow can run.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}
