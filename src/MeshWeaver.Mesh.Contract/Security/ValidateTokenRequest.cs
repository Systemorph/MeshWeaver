using System.Security.Cryptography;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Request to validate an API token. Sent to the token's hub address (ApiToken/{hashPrefix}).
/// The handler on the ApiToken node type validates the hash, checks expiry/revocation,
/// and returns the user info so the caller can build an AccessContext.
/// </summary>
public record ValidateTokenRequest(string RawToken) : IRequest<ValidateTokenResponse>
{
    /// <summary>
    /// Hashes a raw token string using SHA-256. Used to derive the hashPrefix for routing
    /// and for comparing against the stored TokenHash.
    /// </summary>
    public static string HashToken(string rawToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(rawToken);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Prefix that all MeshWeaver API tokens start with.
    /// </summary>
    public const string TokenPrefix = "mw_";
}

/// <summary>
/// Response from token validation. Contains the user identity if the token is valid.
/// </summary>
public record ValidateTokenResponse
{
    /// <summary>User ID from the token.</summary>
    public string? UserId { get; init; }
    /// <summary>Display name from the token.</summary>
    public string? UserName { get; init; }
    /// <summary>Email from the token.</summary>
    public string? UserEmail { get; init; }

    /// <summary>
    /// Roles captured on the <see cref="ApiToken"/> at creation time. The auth
    /// middleware copies these into <see cref="AccessContext.Roles"/> so
    /// SecurityService can resolve permissions via the claim-based role path
    /// even on per-node hubs (where the synced AccessAssignment query is
    /// intentionally not registered — see SecurityServiceExtensions:44-50).
    /// Empty for tokens created before this field existed — those tokens
    /// must be re-created to pick up non-claim role data.
    /// </summary>
    public IReadOnlyCollection<string> Roles { get; init; } = [];

    /// <summary>Error message if validation failed.</summary>
    public string? Error { get; init; }
    /// <summary>Whether validation succeeded.</summary>
    public bool Success => Error == null && UserId != null;

    /// <summary>Creates a successful validation response.</summary>
    public static ValidateTokenResponse Ok(string userId, string userName, string userEmail)
        => new() { UserId = userId, UserName = userName, UserEmail = userEmail };

    /// <summary>
    /// Creates a successful validation response carrying the token's role set so
    /// the auth middleware can stamp <see cref="AccessContext.Roles"/> for
    /// downstream permission checks.
    /// </summary>
    public static ValidateTokenResponse Ok(string userId, string userName, string userEmail,
        IReadOnlyCollection<string> roles)
        => new() { UserId = userId, UserName = userName, UserEmail = userEmail, Roles = roles };

    /// <summary>Creates a failed validation response.</summary>
    public static ValidateTokenResponse Fail(string error)
        => new() { Error = error };
}
