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
    public string? UserId { get; init; }
    public string? UserName { get; init; }
    public string? UserEmail { get; init; }
    public string? Error { get; init; }
    public bool Success => Error == null && UserId != null;

    public static ValidateTokenResponse Ok(string userId, string userName, string userEmail)
        => new() { UserId = userId, UserName = userName, UserEmail = userEmail };

    public static ValidateTokenResponse Fail(string error)
        => new() { Error = error };
}
