namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Content type for API token mesh nodes.
/// Tokens are stored as MeshNodes at path "ApiToken/{hashPrefix}" with nodeType "ApiToken".
/// The raw token is never persisted — only its SHA-256 hash.
/// </summary>
public record ApiToken
{
    /// <summary>SHA-256 hex hash of the raw token.</summary>
    public string TokenHash { get; init; } = "";

    /// <summary>User ObjectId (matches AccessContext.ObjectId).</summary>
    public string UserId { get; init; } = "";

    /// <summary>Display name of the user.</summary>
    public string UserName { get; init; } = "";

    /// <summary>Email of the user.</summary>
    public string UserEmail { get; init; } = "";

    /// <summary>User-defined label, e.g. "Claude Code".</summary>
    public string Label { get; init; } = "";

    /// <summary>When the token was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Optional expiration. Null means no expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Last time the token was used for authentication.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>True if the token has been revoked.</summary>
    public bool IsRevoked { get; init; }

    /// <summary>
    /// Role IDs (e.g. "Admin", "Editor") captured at token-creation time from the creating user's
    /// <c>AccessContext.Roles</c>, returned in <see cref="ValidateTokenResponse.Roles"/> at
    /// validation and stamped onto the request's <c>AccessContext.Roles</c> by the auth middleware.
    ///
    /// <para>🚨 <b>NOT AUTHORITY — a diagnostic breadcrumb.</b> Nothing in
    /// <c>PermissionEvaluator</c> reads it. A token's data permissions are folded from the live
    /// <c>AccessAssignment</c> / <c>PartitionAccessPolicy</c> nodes on the target path, exactly
    /// like a browser session's, and its <see cref="Permission.Api"/> capability is derived from
    /// that path's own public grant and policy cap. Do not reintroduce it into a permission
    /// decision: a value written when the token was minted cannot see a grant made afterwards and
    /// cannot lose a capability revoked afterwards, so trusting it makes both a lockout and a
    /// standing privilege permanent. See Doc/Architecture/AccessControl →
    /// "API tokens and the Api capability".</para>
    /// </summary>
    public IReadOnlyCollection<string> Roles { get; init; } = [];
}
