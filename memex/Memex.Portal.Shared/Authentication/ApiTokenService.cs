using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("MeshWeaver.Auth.Test")]

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Service for creating, validating, and revoking API tokens.
/// Tokens are stored as MeshNodes with nodeType "ApiToken".
/// Raw tokens are never persisted — only their SHA-256 hash.
/// </summary>
internal class ApiTokenService(IMeshNodeFactory nodeFactory, IMeshQuery meshQuery, IMessageHub hub, ILogger<ApiTokenService> logger)
{
    private const string TokenPrefix = "mw_";
    private const int TokenByteLength = 32;
    private const string NodeTypeApiToken = "ApiToken";
    private const string ApiTokenNamespace = "ApiToken";

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

        var created = await nodeFactory.CreateNodeAsync(node, userId);

        logger.LogInformation("Created API token {Label} for user {UserId} (hash prefix {HashPrefix})",
            label, userId, hashPrefix);

        return (rawToken, created);
    }

    public async Task<ApiToken?> ValidateTokenAsync(string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken) || !rawToken.StartsWith(TokenPrefix))
            return null;

        var hash = HashToken(rawToken);
        var hashPrefix = hash[..12];
        var path = $"{ApiTokenNamespace}/{hashPrefix}";

        var node = await meshQuery.QueryAsync<MeshNode>($"path:{path} scope:exact").FirstOrDefaultAsync();
        var apiToken = node?.Content as ApiToken ?? ExtractApiToken(node);
        if (apiToken == null)
            return null;

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
        _ = Task.Run(() =>
        {
            try
            {
                var updated = apiToken with { LastUsedAt = DateTimeOffset.UtcNow };
                var updatedNode = node! with { Content = updated };
                hub.Post(new UpdateNodeRequest(updatedNode));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to update LastUsedAt for token {HashPrefix}", hashPrefix);
            }
        });

        return apiToken;
    }

    public async Task<bool> RevokeTokenAsync(string tokenNodePath)
    {
        var node = await meshQuery.QueryAsync<MeshNode>($"path:{tokenNodePath} scope:exact").FirstOrDefaultAsync();
        if (node == null)
            return false;

        var apiToken = node.Content as ApiToken ?? ExtractApiToken(node);
        if (apiToken == null)
            return false;

        var revoked = apiToken with { IsRevoked = true };
        var updatedNode = node with { Content = revoked };
        hub.Post(new UpdateNodeRequest(updatedNode));

        logger.LogInformation("Revoked API token at {Path}", tokenNodePath);
        return true;
    }

    public async Task<List<ApiTokenInfo>> GetTokensForUserAsync(string userId)
    {
        var tokens = new List<ApiTokenInfo>();

        await foreach (var node in meshQuery.QueryAsync<MeshNode>($"parent:{ApiTokenNamespace} scope:children"))
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

    public static string HashToken(string rawToken)
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
