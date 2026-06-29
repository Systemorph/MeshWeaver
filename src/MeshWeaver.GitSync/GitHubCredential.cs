using System.ComponentModel;

namespace MeshWeaver.GitSync;

/// <summary>
/// A per-user GitHub OAuth credential, stored as a MeshNode at
/// <c>{userId}/_Provider/GitHub</c> on the user's own partition. The
/// <see cref="AccessToken"/> (and optional <see cref="RefreshToken"/>) are
/// encrypted at rest via <see cref="MeshWeaver.AI.IProviderKeyProtector"/>
/// (AES-256-GCM, <c>enc:v1:…</c>) — the same protector that guards LLM provider
/// keys. The token is the <b>committing user's</b>: every push authenticates as
/// this user and the commit is authored as them.
///
/// <para>This is an OUTBOUND credential (replayed verbatim to GitHub), so it must
/// be recoverable — it deliberately does NOT live in the hash-only
/// <c>ApiToken</c> store, which keeps only an irreversible SHA-256.</para>
/// </summary>
public record GitHubCredential
{
    /// <summary>The GitHub OAuth access token, <c>enc:v1:…</c> encrypted. Never logged or echoed.</summary>
    [Browsable(false)]
    public string? AccessToken { get; init; }

    /// <summary>
    /// Optional refresh token (<c>enc:v1:…</c>) — present only when the OAuth app
    /// issues expiring tokens. A classic OAuth App device-flow token is
    /// long-standing (non-expiring) and has none.
    /// </summary>
    [Browsable(false)]
    public string? RefreshToken { get; init; }

    /// <summary>OAuth token type (typically <c>bearer</c>).</summary>
    public string? TokenType { get; init; }

    /// <summary>Granted scopes (e.g. <c>repo</c>), as returned by the token exchange.</summary>
    public string? Scopes { get; init; }

    /// <summary>The authenticated GitHub login (e.g. <c>octocat</c>), for display and commit authoring.</summary>
    public string? GitHubLogin { get; init; }

    /// <summary>When the credential was connected.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Optional expiry. Null means a long-standing (non-expiring) token.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}
