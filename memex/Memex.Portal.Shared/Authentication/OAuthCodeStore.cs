using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// In-memory store for OAuth authorization codes with PKCE support.
/// Codes expire after 5 minutes and are single-use (consumed on exchange).
/// Uses ConcurrentDictionary for thread-safe mutation (per AGENTS.md exception).
/// </summary>
internal class OAuthCodeStore
{
    private readonly ConcurrentDictionary<string, AuthorizationCode> _codes = new();
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Generates a new authorization code and stores it with the given parameters.
    /// </summary>
    public string GenerateCode(
        string userId,
        string userName,
        string userEmail,
        string clientId,
        string redirectUri,
        string? codeChallenge,
        string? codeChallengeMethod)
    {
        // Clean up expired codes opportunistically
        CleanupExpired();

        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        var entry = new AuthorizationCode
        {
            Code = code,
            UserId = userId,
            UserName = userName,
            UserEmail = userEmail,
            ClientId = clientId,
            RedirectUri = redirectUri,
            CodeChallenge = codeChallenge,
            CodeChallengeMethod = codeChallengeMethod,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _codes[code] = entry;
        return code;
    }

    /// <summary>
    /// Exchanges an authorization code for the stored entry.
    /// Returns null if the code is invalid, expired, or already consumed — with the exact
    /// failing check in <paramref name="failureReason"/> so the caller can log a diagnosable
    /// warning (a bare "invalid_grant" made real-world flow failures unattributable).
    /// Validates PKCE code_verifier if a code_challenge was stored.
    /// Consume-first is intentional: a code is single-use on ANY exchange attempt (standard
    /// OAuth hardening — prevents retry brute-force), so a duplicate callback surfaces here
    /// as "unknown or already consumed", not as a second success.
    /// </summary>
    public AuthorizationCode? ExchangeCode(
        string code, string clientId, string redirectUri, string? codeVerifier, out string? failureReason)
    {
        if (!_codes.TryRemove(code, out var entry))
        {
            failureReason = "unknown or already consumed code (never issued by this process, "
                + "expired-and-cleaned, or burnt by an earlier exchange attempt — e.g. a duplicate callback)";
            return null;
        }

        var age = DateTimeOffset.UtcNow - entry.CreatedAt;
        if (age > CodeLifetime)
        {
            failureReason = $"expired: age {(int)age.TotalSeconds}s > lifetime {(int)CodeLifetime.TotalSeconds}s";
            return null;
        }

        if (!string.Equals(entry.ClientId, clientId, StringComparison.Ordinal))
        {
            failureReason = "client_id mismatch between /authorize and /token";
            return null;
        }
        if (!string.Equals(entry.RedirectUri, redirectUri, StringComparison.Ordinal))
        {
            failureReason = "redirect_uri mismatch between /authorize and /token";
            return null;
        }

        if (!string.IsNullOrEmpty(entry.CodeChallenge))
        {
            if (string.IsNullOrEmpty(codeVerifier))
            {
                failureReason = "PKCE code_verifier missing (a code_challenge was supplied at /authorize)";
                return null;
            }

            if (!VerifyPkce(codeVerifier, entry.CodeChallenge, entry.CodeChallengeMethod))
            {
                failureReason = "PKCE verification failed (code_verifier does not match code_challenge)";
                return null;
            }
        }

        failureReason = null;
        return entry;
    }

    private static bool VerifyPkce(string codeVerifier, string codeChallenge, string? method)
    {
        if (string.Equals(method, "S256", StringComparison.OrdinalIgnoreCase))
        {
            var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
            var computed = Convert.ToBase64String(hash)
                .Replace("+", "-").Replace("/", "_").TrimEnd('=');
            return string.Equals(computed, codeChallenge, StringComparison.Ordinal);
        }

        // plain method (or no method specified)
        return string.Equals(codeVerifier, codeChallenge, StringComparison.Ordinal);
    }

    private void CleanupExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - CodeLifetime;
        foreach (var kvp in _codes)
        {
            if (kvp.Value.CreatedAt < cutoff)
                _codes.TryRemove(kvp.Key, out _);
        }
    }
}

internal record AuthorizationCode
{
    public required string Code { get; init; }
    public required string UserId { get; init; }
    public required string UserName { get; init; }
    public required string UserEmail { get; init; }
    public required string ClientId { get; init; }
    public required string RedirectUri { get; init; }
    public string? CodeChallenge { get; init; }
    public string? CodeChallengeMethod { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
