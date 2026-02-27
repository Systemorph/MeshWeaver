using System.Security.Cryptography;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Service for creating, validating, and revoking API tokens.
/// Tokens are stored as MeshNodes with nodeType "ApiToken".
/// Raw tokens are never persisted — only their SHA-256 hash.
/// </summary>
public class ApiTokenService(IPersistenceService persistence, ILogger<ApiTokenService> logger)
{
    private const string TokenPrefix = "mw_";
    private const int TokenByteLength = 32;
    private const string NodeTypeApiToken = "ApiToken";
    private const string ApiTokenNamespace = "ApiToken";

    /// <summary>
    /// Creates a new API token for the specified user.
    /// Returns the raw token (shown once to the user) and the persisted MeshNode.
    /// </summary>
    public async Task<(string RawToken, MeshNode Node)> CreateTokenAsync(
        string userId, string userName, string userEmail, string label, DateTimeOffset? expiresAt = null)
    {
        var rawBytes = RandomNumberGenerator.GetBytes(TokenByteLength);
        var rawToken = TokenPrefix + Convert.ToBase64String(rawBytes)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        var hash = HashToken(rawToken);
        var hashPrefix = hash[..12];

        var apiToken = new ApiToken
        {
            TokenHash = hash,
            UserId = userId,
            UserName = userName,
            UserEmail = userEmail,
            Label = label,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
        };

        var node = new MeshNode(hashPrefix, ApiTokenNamespace)
        {
            Name = $"API Token: {label}",
            NodeType = NodeTypeApiToken,
            State = MeshNodeState.Active,
            Content = apiToken,
        };

        await persistence.SaveNodeAsync(node);

        logger.LogInformation("Created API token {Label} for user {UserId} (hash prefix {HashPrefix})",
            label, userId, hashPrefix);

        return (rawToken, node);
    }

    /// <summary>
    /// Validates a raw token. Returns the ApiToken if valid, null otherwise.
    /// </summary>
    public async Task<ApiToken?> ValidateTokenAsync(string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken) || !rawToken.StartsWith(TokenPrefix))
            return null;

        var hash = HashToken(rawToken);
        var hashPrefix = hash[..12];
        var path = $"{ApiTokenNamespace}/{hashPrefix}";

        var node = await persistence.GetNodeAsync(path);
        if (node?.Content is not ApiToken apiToken)
        {
            // Content might be a JsonElement; try to extract via the node
            apiToken = ExtractApiToken(node);
            if (apiToken == null)
                return null;
        }

        // Verify full hash matches
        if (!string.Equals(apiToken.TokenHash, hash, StringComparison.OrdinalIgnoreCase))
            return null;

        if (apiToken.IsRevoked)
        {
            logger.LogDebug("Token {HashPrefix} is revoked", hashPrefix);
            return null;
        }

        if (apiToken.ExpiresAt.HasValue && apiToken.ExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            logger.LogDebug("Token {HashPrefix} has expired", hashPrefix);
            return null;
        }

        // Update LastUsedAt (fire-and-forget, non-critical)
        _ = Task.Run(async () =>
        {
            try
            {
                var updated = apiToken with { LastUsedAt = DateTimeOffset.UtcNow };
                var updatedNode = node! with { Content = updated };
                await persistence.SaveNodeAsync(updatedNode);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to update LastUsedAt for token {HashPrefix}", hashPrefix);
            }
        });

        return apiToken;
    }

    /// <summary>
    /// Revokes a token by its node path (e.g. "ApiToken/abc123def456").
    /// </summary>
    public async Task<bool> RevokeTokenAsync(string tokenNodePath)
    {
        var node = await persistence.GetNodeAsync(tokenNodePath);
        if (node == null)
            return false;

        var apiToken = node.Content as ApiToken ?? ExtractApiToken(node);
        if (apiToken == null)
            return false;

        var revoked = apiToken with { IsRevoked = true };
        var updatedNode = node with { Content = revoked };
        await persistence.SaveNodeAsync(updatedNode);

        logger.LogInformation("Revoked API token at {Path}", tokenNodePath);
        return true;
    }

    /// <summary>
    /// Gets all tokens for a user. Never returns the raw token or full hash.
    /// </summary>
    public async Task<List<ApiTokenInfo>> GetTokensForUserAsync(string userId)
    {
        var tokens = new List<ApiTokenInfo>();

        await foreach (var node in persistence.GetChildrenAsync(ApiTokenNamespace))
        {
            var apiToken = node.Content as ApiToken ?? ExtractApiToken(node);
            if (apiToken == null || apiToken.UserId != userId)
                continue;

            tokens.Add(new ApiTokenInfo
            {
                NodePath = node.Path,
                Label = apiToken.Label,
                CreatedAt = apiToken.CreatedAt,
                ExpiresAt = apiToken.ExpiresAt,
                LastUsedAt = apiToken.LastUsedAt,
                IsRevoked = apiToken.IsRevoked,
                HashPrefix = apiToken.TokenHash.Length >= 8 ? apiToken.TokenHash[..8] : apiToken.TokenHash,
            });
        }

        return tokens;
    }

    internal static string HashToken(string rawToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(rawToken);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static ApiToken? ExtractApiToken(MeshNode? node)
    {
        if (node?.Content is System.Text.Json.JsonElement jsonElement)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<ApiToken>(jsonElement.GetRawText(),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return null;
            }
        }
        return null;
    }
}

/// <summary>
/// Safe DTO for listing tokens — never exposes the full hash or raw token.
/// </summary>
public record ApiTokenInfo
{
    public string NodePath { get; init; } = "";
    public string Label { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public bool IsRevoked { get; init; }
    public string HashPrefix { get; init; } = "";
}
