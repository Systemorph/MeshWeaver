using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// Reactive token creation — composes two <see cref="IMeshService.CreateNode"/> observables
    /// (hub.Post + RegisterCallback under the hood). No async/await anywhere.
    /// Subscribe to observe the raw token + stored node once both writes commit.
    /// </summary>
    public IObservable<TokenCreationResult> CreateToken(
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

        var userTokenNamespace = $"User/{userId}/{ApiTokenNamespace}";
        var userNode = new MeshNode(hashPrefix, userTokenNamespace)
        {
            Name = $"API Token: {label}",
            NodeType = NodeTypeApiToken,
            State = MeshNodeState.Active,
            Content = apiToken,
        };

        var accessService = hub.ServiceProvider.GetService<AccessService>();

        // Reactive chain: create user node (as current user), then create index node
        // (promoted to System identity). Emits the raw token + created node once both
        // writes commit, or errors on the first failure.
        return nodeFactory.CreateNode(userNode)
            .SelectMany(created =>
            {
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

                // Index writes require System identity (users don't have Create on ApiToken/).
                IObservable<MeshNode> indexObs;
                if (accessService != null)
                {
                    using (accessService.SwitchAccessContext(
                        new AccessContext { ObjectId = WellKnownUsers.System, Name = "system-security" }))
                    {
                        indexObs = nodeFactory.CreateNode(indexNode);
                    }
                }
                else
                {
                    indexObs = nodeFactory.CreateNode(indexNode);
                }

                logger.LogInformation("Creating API token {Label} for user {UserId} (hash prefix {HashPrefix})",
                    label, userId, hashPrefix);

                return indexObs.Select(_ => new TokenCreationResult(rawToken, created));
            });
    }

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

        var created = await nodeFactory.CreateNode(userNode);

        // Store a lightweight index pointer at the original location for O(1) validation lookup.
        // Promote to System identity — users don't have Create permission on the top-level
        // ApiToken/ namespace, but this index is infrastructure (not user data) so it must
        // always be creatable as part of token issuance.
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

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        if (accessService != null)
        {
            using (accessService.SwitchAccessContext(new AccessContext { ObjectId = WellKnownUsers.System, Name = "system-security" }))
            {
                await nodeFactory.CreateNode(indexNode);
            }
        }
        else
        {
            await nodeFactory.CreateNode(indexNode);
        }

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

    /// <summary>
    /// Reactive token revocation — marks the token as revoked via
    /// <see cref="IMeshService.UpdateNode"/> and removes the index pointer.
    /// No async/await. Emits true on success, false if token not found, errors on failure.
    /// </summary>
    public IObservable<bool> RevokeToken(string tokenNodePath) =>
        Observable.FromAsync(() => meshQuery.QueryAsync<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{tokenNodePath}", WellKnownUsers.System))
            .FirstOrDefaultAsync().AsTask())
            .SelectMany(node =>
            {
                var apiToken = node?.Content as ApiToken ?? ExtractApiToken(node);
                if (node == null || apiToken == null)
                    return Observable.Return(false);

                var revoked = apiToken with { IsRevoked = true };
                var updatedNode = node with { Content = revoked };

                // Delete index entry if distinct from the main node.
                if (apiToken.TokenHash.Length >= 12)
                {
                    var hashPrefix = apiToken.TokenHash[..12];
                    var indexPath = $"{ApiTokenNamespace}/{hashPrefix}";
                    if (tokenNodePath != indexPath)
                        hub.Post(new DeleteNodeRequest(indexPath));
                }

                logger.LogInformation("Revoking API token at {Path}", tokenNodePath);
                return nodeFactory.UpdateNode(updatedNode).Select(_ => true);
            });

    /// <summary>
    /// Reactive hard-delete — removes both the primary token node and its index entry.
    /// No async/await. Emits true on success, errors on failure.
    /// </summary>
    public IObservable<bool> DeleteToken(string tokenNodePath) =>
        Observable.FromAsync(() => meshQuery.QueryAsync<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{tokenNodePath}", WellKnownUsers.System))
            .FirstOrDefaultAsync().AsTask())
            .SelectMany(node =>
            {
                var apiToken = node?.Content as ApiToken ?? ExtractApiToken(node);
                var hashPrefix = apiToken?.TokenHash is { Length: >= 12 } h ? h[..12] : null;

                if (!string.IsNullOrEmpty(hashPrefix))
                {
                    var indexPath = $"{ApiTokenNamespace}/{hashPrefix}";
                    if (indexPath != tokenNodePath)
                        hub.Post(new DeleteNodeRequest(indexPath));
                }

                logger.LogInformation("Deleting API token at {Path}", tokenNodePath);
                return nodeFactory.DeleteNode(tokenNodePath);
            });

    /// <summary>
    /// Hard-deletes a token node (and its index entry, if present).
    /// Used to clean up revoked/expired tokens from the UI list.
    /// </summary>
    public async Task DeleteTokenAsync(string tokenNodePath)
    {
        // Look up the node to find the hash prefix so we can clean the index too.
        var node = await QueryAsSystemAsync($"path:{tokenNodePath}").FirstOrDefaultAsync();
        var apiToken = node?.Content as ApiToken ?? ExtractApiToken(node);
        var hashPrefix = apiToken?.TokenHash is { Length: >= 12 } h ? h[..12] : null;

        // Delete the primary token node (under User/{userId}/ApiToken/...)
        hub.Post(new DeleteNodeRequest(tokenNodePath));

        // Delete the index pointer at the top-level ApiToken namespace.
        if (!string.IsNullOrEmpty(hashPrefix))
        {
            var indexPath = $"{ApiTokenNamespace}/{hashPrefix}";
            if (indexPath != tokenNodePath)
                hub.Post(new DeleteNodeRequest(indexPath));
        }

        logger.LogInformation("Deleted API token at {Path}", tokenNodePath);
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
/// Returned by the reactive <see cref="ApiTokenService.CreateToken"/> method
/// once both the user-scoped token node and the index pointer have been created.
/// </summary>
public record TokenCreationResult(string RawToken, MeshNode Node);

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
