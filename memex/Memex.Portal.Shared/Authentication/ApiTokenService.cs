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

        // Capture the caller's roles for the issued token. The previous shape
        // read AccessContext.Roles from the cookie/OAuth principal — but
        // Microsoft OAuth doesn't populate ClaimTypes.Role for personal
        // accounts by default, so the captured set was empty for most users
        // and the resulting API token couldn't do anything. Read the user's
        // self-scope AccessAssignment instead — that's the source-of-truth
        // for "what roles does rbuergi have on User/rbuergi". The assignment
        // sits at User/{userId}/_Access/{userId}_Access (per
        // SecurityCollections convention). Stamped at validation time onto
        // AccessContext.Roles so SecurityService.GetEffectivePermissions
        // resolves them via the claim-based role path on per-node hubs (where
        // the synced AccessAssignment query is intentionally not registered —
        // SecurityServiceExtensions:44-50, recursion avoidance). Empty if no
        // self-scope assignment exists; the token still gets ObjectId so
        // self-owned reads still work, but writes will deny — which is the
        // correct outcome (a user with no role grants no role to their token).
        // Per-user content lives in the user's own partition (Repair v10): paths
        // like rbuergi/ApiToken/{hashPrefix}, NOT User/rbuergi/ApiToken/{hashPrefix}.
        // The dedicated `apitoken` partition still holds the central index node
        // (see indexNode below) — token validation reads the index first then
        // dereferences into the user's own partition.
        var userTokenNamespace = $"{userId}/{ApiTokenNamespace}";
        var assignmentPath = $"{userId}/_Access/{userId}_Access";

        var rolesObs = ResolveSelfScopeRoles(assignmentPath);

        return rolesObs.SelectMany(capturedRoles =>
        {
            var apiToken = new ApiToken
            {
                TokenHash = hash,
                UserId = userId,
                UserName = userName,
                UserEmail = userEmail,
                Label = label,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = expiresAt,
                Roles = capturedRoles,
            };

            var userNode = new MeshNode(hashPrefix, userTokenNamespace)
            {
                Name = $"API Token: {label}",
                NodeType = NodeTypeApiToken,
                State = MeshNodeState.Active,
                MainNode = userId,
                Content = apiToken,
            };

            var accessService = hub.ServiceProvider.GetService<AccessService>();

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

                    // Index writes require System identity — the global ApiToken/
                    // namespace is a separately-gated partition for security
                    // infrastructure that ordinary users don't have Create on. The
                    // previous shape
                    //     using (accessService.SwitchAccessContext(System))
                    //         indexObs = nodeFactory.CreateNode(indexNode);
                    // looked right but was broken: MeshService.CreateNode is
                    // Observable.Defer whose CaptureContext() runs at SUBSCRIBE
                    // time. The using-block disposed synchronously, so by the time
                    // SelectMany below subscribes, the System context had already
                    // been reverted — the deferred CaptureContext returned the
                    // user's context and CreateNodeRequest went out under user
                    // identity → "Create permission required for node
                    // 'ApiToken/{hashPrefix}'".
                    //
                    // Fix: move the SwitchAccessContext INSIDE Observable.Defer
                    // and tie its lifetime to the inner observable via .Finally so
                    // the System context is active during CaptureContext but
                    // reverted promptly when the create completes.
                    IObservable<MeshNode> indexObs;
                    if (accessService != null)
                    {
                        indexObs = Observable.Defer(() =>
                        {
                            var disp = accessService.SwitchAccessContext(
                                new AccessContext { ObjectId = WellKnownUsers.System, Name = "system-security" });
                            return nodeFactory.CreateNode(indexNode).Finally(() => disp.Dispose());
                        });
                    }
                    else
                    {
                        indexObs = nodeFactory.CreateNode(indexNode);
                    }

                    logger.LogInformation("Creating API token {Label} for user {UserId} (hash prefix {HashPrefix})",
                        label, userId, hashPrefix);

                    return indexObs.Select(_ => new TokenCreationResult(rawToken, created));
                });
        });
    }

    /// <summary>
    /// Reads the user's self-scope <see cref="AccessAssignment"/> at
    /// <c>User/{userId}/_Access/{userId}_Access</c> and emits the (non-denied)
    /// role IDs assigned there. Read is performed under System identity so
    /// it succeeds regardless of whether the calling user has Read on
    /// AccessAssignments. Emits an empty array when the assignment doesn't
    /// exist or the read fails — token creation continues with no captured
    /// roles, which is the correct outcome for a user with no role grants
    /// (the issued token has identity but no permissions).
    /// </summary>
    private IObservable<IReadOnlyCollection<string>> ResolveSelfScopeRoles(string assignmentPath)
    {
        return Observable.FromAsync(async () =>
        {
            try
            {
                await foreach (var node in QueryAsSystemAsync($"path:{assignmentPath}"))
                {
                    AccessAssignment? assignment = node.Content as AccessAssignment;
                    if (assignment == null && node.Content is System.Text.Json.JsonElement je)
                    {
                        try
                        {
                            assignment = System.Text.Json.JsonSerializer.Deserialize<AccessAssignment>(
                                je.GetRawText(), hub.JsonSerializerOptions);
                        }
                        catch { /* fall through */ }
                    }
                    if (assignment == null) continue;
                    var roles = assignment.Roles
                        .Where(r => !r.Denied && !string.IsNullOrEmpty(r.Role))
                        .Select(r => r.Role)
                        .Distinct()
                        .ToArray();
                    return (IReadOnlyCollection<string>)roles;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to resolve self-scope roles from {Path} for token creation; continuing with empty role set",
                    assignmentPath);
            }
            return (IReadOnlyCollection<string>)Array.Empty<string>();
        });
    }


    /// <summary>
    /// Queries nodes using the system identity to bypass access control.
    /// ApiTokenService is infrastructure code that needs unrestricted read access.
    /// </summary>
    private IAsyncEnumerable<MeshNode> QueryAsSystemAsync(string query, CancellationToken ct = default)
        => meshQuery.QueryAsync<MeshNode>(
            MeshQueryRequest.FromQuery(query, WellKnownUsers.System), ct: ct);

    /// <summary>
    /// Reactive token validation — synchronous method returning <see cref="IObservable{T}"/>.
    /// The Task-returning <c>await ValidateTokenAsync</c> shape deadlocks when invoked from
    /// hub-reachable code (the <see cref="IAsyncEnumerable{T}"/> iteration flows through a
    /// hub round-trip; awaiting it captures the dispatch SyncContext and blocks the action
    /// block waiting for itself). Returning <see cref="IObservable{T}"/> lets callers compose
    /// without bridging back to <see cref="Task"/> mid-flow. See CLAUDE.md "NOTHING ASYNC EVER".
    /// </summary>
    public IObservable<ApiToken?> ValidateToken(string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken) || !rawToken.StartsWith(TokenPrefix))
            return Observable.Return<ApiToken?>(null);

        var hash = HashToken(rawToken);
        var hashPrefix = hash[..12];
        var indexPath = $"{ApiTokenNamespace}/{hashPrefix}";

        return Observable.FromAsync(() => QueryAsSystemAsync($"path:{indexPath}")
                .FirstOrDefaultAsync().AsTask())
            .SelectMany(indexNode =>
            {
                if (indexNode == null)
                    return Observable.Return<(MeshNode? node, ApiToken? token)>((null, null));

                var index = indexNode.Content as ApiTokenIndex ?? ExtractApiTokenIndex(indexNode);
                if (index != null)
                {
                    if (!string.Equals(index.TokenHash, hash, StringComparison.OrdinalIgnoreCase))
                        return Observable.Return<(MeshNode? node, ApiToken? token)>((null, null));
                    return Observable.FromAsync(() =>
                            QueryAsSystemAsync($"path:{index.TokenPath}")
                                .FirstOrDefaultAsync().AsTask())
                        .Select(tn => (node: tn,
                            token: (tn?.Content as ApiToken) ?? ExtractApiToken(tn)));
                }
                // Legacy format: full ApiToken at index path
                return Observable.Return((node: (MeshNode?)indexNode,
                    token: (indexNode.Content as ApiToken) ?? ExtractApiToken(indexNode)));
            })
            .Select(t => FinalizeToken(t.node, t.token, hash, hashPrefix));
    }

    private ApiToken? FinalizeToken(MeshNode? tokenNode, ApiToken? apiToken, string hash, string hashPrefix)
    {
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

        // Update LastUsedAt (fire-and-forget, non-critical).
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
    /// ApiToken nodes don't have a per-node hub activated, so the lookup goes through
    /// the mesh-level read-side index (system identity); <c>hub.GetMeshNode</c> would
    /// hang waiting for a route to a non-activatable address.
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
    /// Same rationale as <see cref="RevokeToken"/>: ApiToken nodes have no per-node
    /// hub, so we use the mesh-level read-side index for the lookup.
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

        // Query user-scoped tokens (post-v10 path: per-user partition).
        // ApiToken is a satellite type (MainNode != Path), so we need nodeType: condition
        // to trigger GetAllChildrenAsync which includes satellites in the results.
        var userTokenNamespace = $"{userId}/{ApiTokenNamespace}";
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
