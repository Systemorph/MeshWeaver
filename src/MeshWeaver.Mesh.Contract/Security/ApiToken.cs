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
}
