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
internal class ApiTokenService(IMeshService nodeFactory, IMeshService meshQuery, IMessageHub hub, ILogger<ApiTokenService> logger)
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

        // Store the full token under the user's namespace
        var userTokenNamespace = $"User/{userId}/{ApiTokenNamespace}";
        var userNode = new MeshNode(hashPrefix, userTokenNamespace)
        {
            Name = $"API Token: {label}",
            NodeType = NodeTypeApiToken,
            State = MeshNodeState.Active,
            Content = apiToken,
        };

        var created = await nodeFactory.CreateNodeAsync(userNode);

        // Store a lightweight index pointer at the original location for O(1) validation lookup
        var indexNode = new MeshNode(hashPrefix, ApiTokenNamespace)
        {
            Name = $"API Token: {label}",
            NodeType = NodeTypeApiToken,
            State = MeshNodeState.Active,
            Content = new ApiTokenIndex
            {
                TokenHash = hash,
                TokenPath = created.Path,
            },
        };

        await nodeFactory.CreateNodeAsync(indexNode);

        logger.LogInformation("Created API token {Label} for user {UserId} (hash prefix {HashPrefix})",
            label, userId, hashPrefix);

        return (rawToken, created);
    }

    /// <summary>
    /// Queries nodes using the system identity to bypass access control.
    /// ApiTokenService is infrastructure code that needs unrestricted read access.
    /// </summary>
    private IAsyncEnumerable<MeshNode> QueryAsSystemAsync(string query, CancellationToken ct = default)
        => meshQuery.QueryAsync<MeshNode>(
            MeshQueryRequest.FromQuery(query, WellKnownUsers.System), ct: ct);

    public async Task<ApiToken?> ValidateTokenAsync(string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken) || !rawToken.StartsWith(TokenPrefix))
            return null;

        var hash = HashToken(rawToken);
        var hashPrefix = hash[..12];
        var indexPath = $"{ApiTokenNamespace}/{hashPrefix}";

        var indexNode = await QueryAsSystemAsync($"path:{indexPath}").FirstOrDefaultAsync();
        if (indexNode == null)
            return null;

        // Follow index pointer to the full token, or handle legacy tokens directly
        MeshNode? tokenNode;
        ApiToken? apiToken;
        var index = indexNode.Content as ApiTokenIndex ?? ExtractApiTokenIndex(indexNode);
        if (index != null)
        {
            // New format: index pointer -> follow to user namespace
            if (!string.Equals(index.TokenHash, hash, StringComparison.OrdinalIgnoreCase))
                return null;
            tokenNode = await QueryAsSystemAsync($"path:{index.TokenPath}").FirstOrDefaultAsync();
            apiToken = tokenNode?.Content as ApiToken ?? ExtractApiToken(tokenNode);
        }
        else
        {
            // Legacy format: full ApiToken at index path
            tokenNode = indexNode;
            apiToken = indexNode.Content as ApiToken ?? ExtractApiToken(indexNode);
        }

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
        try
        {
            var updated = apiToken with { LastUsedAt = DateTimeOffset.UtcNow };
            var updatedNode = tokenNode! with { Content = updated };
            hub.Post(new UpdateNodeRequest(updatedNode));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to update LastUsedAt for token {HashPrefix}", hashPrefix);
        }

        return apiToken;
    }

    public async Task<bool> RevokeTokenAsync(string tokenNodePath)
    {
        var node = await QueryAsSystemAsync($"path:{tokenNodePath}").FirstOrDefaultAsync();
        if (node == null)
            return false;

        var apiToken = node.Content as ApiToken ?? ExtractApiToken(node);
        if (apiToken == null)
            return false;

        var revoked = apiToken with { IsRevoked = true };
        var updatedNode = node with { Content = revoked };
        hub.Post(new UpdateNodeRequest(updatedNode));

        // Also revoke the index node at ApiToken/{hashPrefix} if it exists
        if (apiToken.TokenHash.Length >= 12)
        {
            var hashPrefix = apiToken.TokenHash[..12];
            var indexPath = $"{ApiTokenNamespace}/{hashPrefix}";
            if (tokenNodePath != indexPath)
            {
                var indexNode = await QueryAsSystemAsync($"path:{indexPath}").FirstOrDefaultAsync();
                if (indexNode != null)
                {
                    hub.Post(new DeleteNodeRequest(indexPath));
                }
            }
        }

        logger.LogInformation("Revoked API token at {Path}", tokenNodePath);
        return true;
    }

    public async Task<List<ApiTokenInfo>> GetTokensForUserAsync(string userId)
    {
        var tokens = new List<ApiTokenInfo>();

        // Query user-scoped tokens (new format)
        // ApiToken is a satellite type (MainNode != Path), so we need nodeType: condition
        // to trigger GetAllChildrenAsync which includes satellites in the results.
        var userTokenNamespace = $"User/{userId}/{ApiTokenNamespace}";
        await foreach (var node in QueryAsSystemAsync($"namespace:{userTokenNamespace} nodeType:{NodeTypeApiToken}"))
        {
            var apiToken = node.Content as ApiToken ?? ExtractApiToken(node);
            if (apiToken == null)
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

        // Fallback: also check legacy tokens at top-level ApiToken namespace
        await foreach (var node in QueryAsSystemAsync($"namespace:{ApiTokenNamespace} nodeType:{NodeTypeApiToken}"))
        {
            var apiToken = node.Content as ApiToken ?? ExtractApiToken(node);
            if (apiToken == null || apiToken.UserId != userId)
                continue;

            // Skip if we already found this token in the user namespace
            var hashPrefix = apiToken.TokenHash.Length >= 8 ? apiToken.TokenHash[..8] : apiToken.TokenHash;
            if (tokens.Any(t => t.HashPrefix == hashPrefix))
                continue;

            tokens.Add(new ApiTokenInfo
            {
                NodePath = node.Path,
                Label = apiToken.Label,
                CreatedAt = apiToken.CreatedAt,
                ExpiresAt = apiToken.ExpiresAt,
                LastUsedAt = apiToken.LastUsedAt,
                IsRevoked = apiToken.IsRevoked,
                HashPrefix = hashPrefix,
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

    private static ApiTokenIndex? ExtractApiTokenIndex(MeshNode? node)
    {
        if (node?.Content is System.Text.Json.JsonElement jsonElement)
        {
            try
            {
                var index = System.Text.Json.JsonSerializer.Deserialize<ApiTokenIndex>(jsonElement.GetRawText(),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                // Distinguish from legacy ApiToken: index has TokenPath, ApiToken does not
                return !string.IsNullOrEmpty(index?.TokenPath) ? index : null;
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
