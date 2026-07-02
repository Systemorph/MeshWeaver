using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("MeshWeaver.Auth.Test")]
[assembly: InternalsVisibleTo("Memex.Portal.Shared.Test")]

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Service for creating, validating, and revoking API tokens.
/// Tokens are stored as MeshNodes with nodeType "ApiToken". Raw tokens
/// are never persisted — only their SHA-256 hash.
///
/// <para>
/// 🚨 No async / Task / FromAsync / await anywhere in this file. Every
/// reachable method returns <see cref="IObservable{T}"/> and the chain
/// stays observable end-to-end. Reads of known paths go through
/// <c>hub.GetMeshNode(path)</c> (one-shot) or
/// <c>workspace.GetMeshNodeStream(path)</c> (live); listings go through
/// <c>workspace.GetQuery(id, queries...)</c> (synced + path-keyed dedup).
/// QueryAsync / <see cref="IAsyncEnumerable{T}"/> iteration is forbidden
/// in this file per <c>Doc/Architecture/AsynchronousCalls.md</c> and
/// <c>Doc/Architecture/SyncedMeshNodeQueries.md</c>.
/// </para>
/// </summary>
internal class ApiTokenService(IMeshService nodeFactory, IMessageHub hub, ILogger<ApiTokenService> logger)
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

        // Per-user partition layout (Repair v10): tokens live at
        // {userId}/ApiToken/{hashPrefix}, NOT under User/{userId}/ApiToken.
        // The global ApiToken/{hashPrefix} index entry routes incoming
        // bearer tokens to the right user-scoped node at validation time.
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
                    // infrastructure that ordinary users don't have Create on.
                    // See git history on this file for the SwitchAccessContext-
                    // outside-Defer bug that this lambda layout fixes (System
                    // context must be active during CaptureContext at Subscribe
                    // time, not when the outer using-block returned).
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
    /// <c>{userId}/_Access/{userId}_Access</c> and emits the (non-denied)
    /// role IDs assigned there. Pure observable composition — one-shot
    /// <see cref="MeshNodeStreamExtensions.GetMeshNode"/> under System
    /// identity, then <c>.Select</c>. Emits an empty array on missing
    /// assignment or read failure (the issued token still has identity
    /// but no role grants — correct outcome).
    /// </summary>
    private IObservable<IReadOnlyCollection<string>> ResolveSelfScopeRoles(string assignmentPath)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();

        // Observable.Using ties the AsyncLocal System scope's lifetime to
        // the Subscribe of the inner observable, not to the lambda body's
        // return — same shape used by ApiTokenNodeType.HandleValidateToken
        // for the same reason (Defer-style subscribe-time capture).
        var readUnderSystem = accessService != null
            ? Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => hub.GetMeshNode(assignmentPath, TimeSpan.FromSeconds(5)))
            : hub.GetMeshNode(assignmentPath, TimeSpan.FromSeconds(5));

        return readUnderSystem
            .Select(node =>
            {
                var assignment = node?.Content as AccessAssignment ?? ExtractAccessAssignment(node);
                if (assignment is null)
                    return (IReadOnlyCollection<string>)Array.Empty<string>();
                return assignment.Roles
                    .Where(r => !r.Denied && !string.IsNullOrEmpty(r.Role))
                    .Select(r => r.Role)
                    .Distinct()
                    .ToArray();
            })
            .Catch<IReadOnlyCollection<string>, Exception>(ex =>
            {
                logger.LogWarning(ex,
                    "Failed to resolve self-scope roles from {Path} for token creation; continuing with empty role set",
                    assignmentPath);
                return Observable.Return<IReadOnlyCollection<string>>(Array.Empty<string>());
            });
    }

    /// <summary>
    /// Reactive token validation. Reads index node at
    /// <c>ApiToken/{hashPrefix}</c> via <c>hub.GetMeshNode</c> (one-shot,
    /// authoritative — never <c>QueryAsync</c> for a known path per
    /// <c>Doc/Architecture/AsynchronousCalls.md</c>); when the index
    /// points at a user-scoped token, follows the pointer with a second
    /// one-shot read. The chain is fully observable — no
    /// <c>FromAsync</c>, no <c>FirstOrDefaultAsync.AsTask()</c>, no
    /// <c>await</c>.
    /// </summary>
    public IObservable<ApiToken?> ValidateToken(string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken) || !rawToken.StartsWith(TokenPrefix))
            return Observable.Return<ApiToken?>(null);

        var hash = HashToken(rawToken);
        var hashPrefix = hash[..12];
        var indexPath = $"{ApiTokenNamespace}/{hashPrefix}";

        return ReadAsSystem(indexPath)
            .SelectMany(indexNode =>
            {
                if (indexNode == null)
                    return Observable.Return<(MeshNode? node, ApiToken? token)>((null, null));

                var index = indexNode.Content as ApiTokenIndex ?? ExtractApiTokenIndex(indexNode);
                if (index != null)
                {
                    if (!string.Equals(index.TokenHash, hash, StringComparison.OrdinalIgnoreCase))
                        return Observable.Return<(MeshNode? node, ApiToken? token)>((null, null));
                    return ReadAsSystem(index.TokenPath)
                        .Select(tn => (
                            node: tn,
                            token: (tn?.Content as ApiToken) ?? ExtractApiToken(tn)));
                }
                // Legacy format: full ApiToken at index path.
                return Observable.Return((
                    node: (MeshNode?)indexNode,
                    token: (indexNode.Content as ApiToken) ?? ExtractApiToken(indexNode)));
            })
            .Select(t => FinalizeToken(t.node, t.token, hash, hashPrefix));
    }

    private IObservable<MeshNode?> ReadAsSystem(string path)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return accessService != null
            ? Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => hub.GetMeshNode(path, TimeSpan.FromSeconds(5)))
            : hub.GetMeshNode(path, TimeSpan.FromSeconds(5));
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

        // Update LastUsedAt via the canonical workspace remote stream —
        // fire-and-forget (non-critical telemetry). Subscribe is mandatory
        // because Update is cold; the empty error handler keeps the cold
        // observable's GC-time fire-and-forget warning quiet on writes
        // that hit a deleted node.
        //
        // Stamp at DISPLAY granularity, not per request. The UI renders "Last used" to the
        // minute; a per-request write turned a busy integration's token into the hottest node
        // on the mesh (atioz 2026-07-02: version 8939 in one day, ~13 writes/min), and every
        // write fans out through the change feed to all of the node's subscriber streams.
        // Skip the write while the recorded LastUsedAt is fresh — the read above already
        // carries the current value.
        if (tokenNode != null
            && (apiToken.LastUsedAt is null
                || DateTimeOffset.UtcNow - apiToken.LastUsedAt.Value >= LastUsedStampInterval))
        {
            hub.GetWorkspace()
                .GetMeshNodeStream(tokenNode.Path)
                .Update(node => node with { Content = (node.Content as ApiToken ?? apiToken) with { LastUsedAt = DateTimeOffset.UtcNow } })
                .Subscribe(_ => { }, _ => { });
        }

        return apiToken;
    }

    /// <summary>
    /// Minimum age of the recorded <see cref="ApiToken.LastUsedAt"/> before a token use writes a
    /// fresh stamp. Keeps the "Last used" display accurate to a few minutes without making every
    /// authenticated request a mesh-node write.
    /// </summary>
    private static readonly TimeSpan LastUsedStampInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Reactive token revocation. Writes the IsRevoked flag through
    /// <c>workspace.GetMeshNodeStream(path).Update(...)</c> — the
    /// canonical remote-stream write per
    /// <c>Doc/Architecture/AsynchronousCalls.md</c>. No
    /// <c>UpdateNodeRequest</c> forwarding (the previous, now-retired shape
    /// timed out in distributed deployments when the per-node hub's
    /// forwarded request didn't get a response within ~30s).
    ///
    /// <para>The global index entry is hard-deleted as a fire-and-forget
    /// side effect — the index miss is a defense-in-depth gate on top of
    /// the authoritative <c>IsRevoked</c> flag, not a primary requirement
    /// for the revoke to be effective.</para>
    /// </summary>
    public IObservable<bool> RevokeToken(string tokenNodePath)
    {
        var workspace = hub.GetWorkspace();
        var indexPath = DeriveIndexPath(tokenNodePath);

        logger.LogInformation("Revoking API token at {Path}", tokenNodePath);

        var primary = workspace.GetMeshNodeStream(tokenNodePath)
            .Update(current =>
            {
                var token = current.Content as ApiToken ?? ExtractApiToken(current);
                if (token == null) return current;
                // Flip IsRevoked on the live node — validation reads the node fresh (no cache),
                // so the revoke takes effect immediately.
                return current with { Content = token with { IsRevoked = true } };
            })
            .Do(updatedNode =>
            {
                // Force the per-node hub to persist the patched node. The
                // sync-protocol path (workspace.GetMeshNodeStream(remote)
                // .Update) updates the mesh-hub-side stream and emits a
                // DataChangeRequest to the per-node hub, but the per-node
                // hub's data source `saveSub` only fires on `ownStream`
                // emissions — and those don't fire for sync-driven changes,
                // so persistence never sees the IsRevoked=true update. The
                // SaveMeshNodeRequest below routes to the per-node hub's
                // HandleSaveMeshNode which writes through IStorageService
                // (firing IDataChangeNotifier.Updated, so the synced
                // GetTokensForUser view picks up the change).
                hub.Post(new SaveMeshNodeRequest(updatedNode),
                    o => o.WithTarget(new Address(tokenNodePath)));
            })
            .Select(_ => true)
            .Catch<bool, Exception>(ex =>
            {
                logger.LogWarning(ex, "RevokeToken failed for {Path}", tokenNodePath);
                return Observable.Return(false);
            });

        // Chain the global-index delete into the returned observable rather
        // than firing a separate Subscribe — see the matching comment in
        // DeleteToken. A missing index entry is fine: the Catch returns false
        // and the primary revoke result wins.
        if (indexPath == null || indexPath == tokenNodePath)
            return primary;

        return primary.SelectMany(result =>
            nodeFactory.DeleteNode(indexPath)
                .Catch<bool, Exception>(_ => Observable.Return(false))
                .Select(_ => result));
    }

    /// <summary>
    /// Reactive hard-delete. Removes the user-scoped token node and the
    /// global index entry (fire-and-forget). The user-scoped delete goes
    /// through <see cref="IMeshService.DeleteNode"/>; this is the
    /// authoritative removal and the only outcome the caller observes.
    /// </summary>
    public IObservable<bool> DeleteToken(string tokenNodePath)
    {
        var indexPath = DeriveIndexPath(tokenNodePath);

        logger.LogInformation("Deleting API token at {Path}", tokenNodePath);

        var primary = nodeFactory.DeleteNode(tokenNodePath)
            .Select(_ => true)
            .Catch<bool, Exception>(ex =>
            {
                logger.LogWarning(ex, "DeleteToken failed for {Path}", tokenNodePath);
                return Observable.Return(false);
            });

        // Chain the index-entry delete into the returned observable rather than
        // firing a separate Subscribe. The previous shape leaked a pending
        // hub.Observe callback past test dispose — the response arrives only
        // after routing surfaces NotFound (~15ms+) but the test's await
        // completes faster, so the dispose-time Quiescing watchdog flags the
        // pending callback as a leaked subscription. Chaining here also makes
        // a missing-index case (token already gone) a non-failure of the whole
        // operation: the inner Catch swallows it and the primary result wins.
        if (indexPath == null || indexPath == tokenNodePath)
            return primary;

        return primary.SelectMany(result =>
            nodeFactory.DeleteNode(indexPath)
                .Catch<bool, Exception>(_ => Observable.Return(false))
                .Select(_ => result));
    }

    /// <summary>
    /// Live list of the user's tokens via the canonical synced query
    /// (<c>workspace.GetQuery</c>). The synced query gives us path-keyed
    /// dedup across the user-scope and legacy global namespaces,
    /// all-Initial gating, and provider fan-out — see
    /// <c>Doc/Architecture/SyncedMeshNodeQueries.md</c>. The cache id is
    /// per-user so re-mounts (settings tab re-render) reuse the upstream
    /// subscription instead of cycling Initial waves.
    /// </summary>
    public IObservable<IReadOnlyList<ApiTokenInfo>> GetTokensForUser(string userId)
    {
        var workspace = hub.GetWorkspace();
        var userTokenNamespace = $"{userId}/{ApiTokenNamespace}";

        return workspace.GetQuery(
                $"api-tokens:{userId}",
                $"namespace:{userTokenNamespace} nodeType:{NodeTypeApiToken}",
                // Legacy fallback: tokens at the global ApiToken namespace
                // that pre-date the per-user partition migration. Filtered
                // by UserId in the projection below — the synced query
                // can't express that predicate, so we over-fetch globally
                // and prune.
                $"namespace:{ApiTokenNamespace} nodeType:{NodeTypeApiToken}")
            .Select(snapshot =>
            {
                var tokens = new List<ApiTokenInfo>();
                var seenPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var node in snapshot)
                {
                    if (node.Path is null) continue;
                    var apiToken = node.Content as ApiToken ?? ExtractApiToken(node);
                    if (apiToken == null) continue;

                    // Legacy nodes in the global namespace must match the
                    // calling userId; per-user-partition nodes are scoped
                    // by namespace and don't need this filter, but the
                    // check is cheap and unifies the projection.
                    if (apiToken.UserId != userId) continue;

                    var hashPrefix = apiToken.TokenHash.Length >= 8
                        ? apiToken.TokenHash[..8]
                        : apiToken.TokenHash;
                    if (!seenPrefixes.Add(hashPrefix)) continue;

                    tokens.Add(ToInfo(node, apiToken));
                }
                return (IReadOnlyList<ApiTokenInfo>)tokens;
            });
    }

    /// <summary>
    /// Derives the global <c>ApiToken/{hashPrefix}</c> index path from a
    /// user-scoped token node path. <see cref="CreateToken"/> sets the
    /// node Id to the 12-char hash prefix, so the last path segment is
    /// reliably the prefix used to build the index entry. Returns null
    /// for malformed paths (no slash, trailing slash).
    /// </summary>
    private static string? DeriveIndexPath(string tokenNodePath)
    {
        if (string.IsNullOrEmpty(tokenNodePath)) return null;
        var lastSlash = tokenNodePath.LastIndexOf('/');
        if (lastSlash < 0 || lastSlash >= tokenNodePath.Length - 1) return null;
        var hashPrefix = tokenNodePath[(lastSlash + 1)..];
        return $"{ApiTokenNamespace}/{hashPrefix}";
    }

    private static ApiTokenInfo ToInfo(MeshNode node, ApiToken apiToken) => new()
    {
        NodePath = node.Path,
        Label = apiToken.Label,
        CreatedAt = apiToken.CreatedAt,
        ExpiresAt = apiToken.ExpiresAt,
        LastUsedAt = apiToken.LastUsedAt,
        IsRevoked = apiToken.IsRevoked,
        HashPrefix = apiToken.TokenHash.Length >= 8 ? apiToken.TokenHash[..8] : apiToken.TokenHash,
    };

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

    private AccessAssignment? ExtractAccessAssignment(MeshNode? node)
    {
        if (node?.Content is AccessAssignment direct) return direct;
        if (node?.Content is System.Text.Json.JsonElement jsonElement)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<AccessAssignment>(
                    jsonElement.GetRawText(), hub.JsonSerializerOptions);
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
