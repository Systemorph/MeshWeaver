using System.Collections.Concurrent;
using System.Diagnostics;
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
/// <c>hub.GetMeshNode(path)</c> (one-shot, issuance-side) or the bounded
/// resilient <c>workspace.GetMeshNodeStream(path)</c> poll
/// (validation-side, <see cref="ReadValidationNode"/>); listings go through
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
    /// Single-flight recency memo for the LastUsedAt stamp, keyed by token hash:
    /// the UTC time this service instance last DISPATCHED a stamp write for the
    /// token. Recorded at dispatch (not completion), so a burst of validations
    /// re-dispatches nothing while a stamp is still in flight — READ-SIDE state
    /// (query snapshot, stream mirror) can lag arbitrarily and must never gate
    /// the write (CI runs 28682878901/28684288201: the snapshot-only and
    /// mirror-lambda gates both let lagged polls through, 5 then 3 duplicate
    /// stamps on one token node). Instance field on the mesh-scoped singleton
    /// (AddSingleton&lt;ApiTokenService&gt;) — dies with the mesh, never static.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastStampDispatchedAt = new();

    private int stampDispatchCount;

    /// <summary>
    /// Upper bound on each of the two validation-side node reads (index at
    /// <c>ApiToken/{hashPrefix}</c>, then the user-scoped token node). The read is the
    /// resilient <c>GetMeshNodeStream</c> poll (same shape as
    /// <c>OAuthCodeStore.ReadCodeNode</c>), so a WARM token resolves on the first
    /// attempt with no added latency — only a genuine miss (unknown token, or a fresh
    /// token still inside the create→routable window on THIS replica) keeps polling
    /// and burns the window. 8 s default: ample margin over the observed cross-replica
    /// create→routable lag on memex-cloud (a fresh token 401ed for ~2 min under the
    /// old one-shot read; the poll rides the lag out instead). Init-only so tests can
    /// shorten it — never a static bound to "tune away" a failure.
    /// </summary>
    public TimeSpan ValidationReadTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Pause between validation read attempts — matches <c>OAuthCodeStore.ReadCodeNode</c>
    /// and <see cref="ConfirmReadable"/> (self-paced Concat: the next attempt subscribes
    /// only after the previous one completed, so exactly one owner-hub subscription is
    /// live at a time).
    /// </summary>
    private static readonly TimeSpan ValidationRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Number of LastUsedAt stamp writes this instance has DISPATCHED — the
    /// single-flight contract's observable. A burst of N validations against
    /// a token must move this by exactly 1 per freshness window regardless of
    /// read-side lag; <c>ApiTokenServiceTests</c> asserts on it (node-version
    /// observation alone cannot distinguish "one write" from "several writes
    /// coalesced by the change feed" deterministically).
    /// </summary>
    internal int StampDispatchCount => Volatile.Read(ref stampDispatchCount);

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

                    // 🚨 Confirm BOTH nodes are readable before returning the raw token. On a
                    // multi-silo portal a just-created node is not instantly routable: the /token
                    // exchange issues the token on one replica and the MCP client reconnects
                    // (validates) near-instantly, often on ANOTHER replica, where ValidateToken's
                    // one-shot hub.GetMeshNode hits [ROUTE] NotFound during the create→routable
                    // window → the token is rejected ("Got new credentials, but memex rejected them
                    // on reconnect"). Reading each node back through the resilient GetMeshNodeStream
                    // poll activates its owning per-node hub from Postgres, so by the time /token
                    // returns the token is validatable on any replica. Keeps the HOT validation path
                    // on the fast one-shot read (only issuance — a one-time login — pays this).
                    return indexObs.SelectMany(_ =>
                        ConfirmReadable(indexNode.Path)
                            .SelectMany(__ => ConfirmReadable(created.Path))
                            .Select(___ => new TokenCreationResult(rawToken, created)));
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
    /// <para>ISSUANCE-ONLY — called exclusively from <see cref="CreateToken"/>,
    /// never on the validation path (Bearer-request role enrichment goes
    /// through <c>UserRoleResolver.LoadDbRolesAsync</c> → the synced query),
    /// so the one-shot read's timeout cannot 401 anybody; a miss only means
    /// the new token carries no captured roles. Do not "harden" it with the
    /// validation-side resilient poll — issuance is a one-time login that
    /// must not stall on a genuinely absent assignment.</para>
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
    /// Reactive token validation. Reads the index node at
    /// <c>ApiToken/{hashPrefix}</c> — and, when it points at a user-scoped token,
    /// the token node — through the bounded resilient
    /// <c>GetMeshNodeStream</c> poll (<see cref="ReadValidationNode"/>, same shape
    /// as <c>OAuthCodeStore.ReadCodeNode</c>), NOT a one-shot <c>hub.GetMeshNode</c>.
    /// <para>
    /// Why: on a multi-replica portal a freshly-minted token is not instantly
    /// routable on the replicas that did NOT mint it. The old one-shot read hit
    /// [ROUTE] NotFound for the full 5 s and then emitted null SILENTLY → every
    /// /mcp reconnect on pods B/C returned 401 for ~2 minutes with zero server-side
    /// trace (memex-cloud 2026-07-24, ingress logs: every rejected request took
    /// exactly 5.000–5.007 s). The issuance-side warm-up (#624) only helps the
    /// minting pod. The resilient read activates the owning per-node hub from
    /// shared Postgres and rides out the lag; a warm token resolves on the first
    /// attempt, so the hot path pays nothing.
    /// </para>
    /// <para>
    /// Every failure is logged at Warning with the hash prefix (never the raw
    /// token), the failing stage (index-read-timeout / index-hash-mismatch /
    /// token-read-timeout / token-hash-mismatch / revoked / expired) and elapsed ms.
    /// The chain is fully observable — no <c>FromAsync</c>, no <c>await</c>.
    /// </para>
    /// </summary>
    public IObservable<ApiToken?> ValidateToken(string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken) || !rawToken.StartsWith(TokenPrefix))
            return Observable.Return<ApiToken?>(null);

        var hash = HashToken(rawToken);
        var hashPrefix = hash[..12];
        var indexPath = $"{ApiTokenNamespace}/{hashPrefix}";

        return Observable.Defer(() =>
        {
            var elapsed = Stopwatch.StartNew();
            return ReadValidationNode(indexPath, node => IndexHashVerdict(node, hash), "index", hashPrefix, elapsed)
                .SelectMany(indexNode =>
                {
                    if (indexNode == null)
                        return Observable.Return<(MeshNode? node, ApiToken? token)>((null, null));

                    var index = indexNode.Content as ApiTokenIndex ?? ExtractApiTokenIndex(indexNode);
                    if (index != null)
                        return ReadValidationNode(
                                index.TokenPath, node => TokenHashVerdict(node, hash), "token", hashPrefix, elapsed)
                            .Select(tn => (
                                node: tn,
                                token: (tn?.Content as ApiToken) ?? ExtractApiToken(tn)));

                    // Legacy format: full ApiToken at the index path (its hash already
                    // matched in the verdict — a mismatch would have terminated above).
                    return Observable.Return((
                        node: (MeshNode?)indexNode,
                        token: (indexNode.Content as ApiToken) ?? ExtractApiToken(indexNode)));
                })
                .Select(t => FinalizeToken(t.node, t.token, hash, hashPrefix, elapsed));
        });
    }

    /// <summary>
    /// Verdict for one successfully-read candidate node during validation:
    /// <c>true</c> = expected content present with the MATCHING hash (emit the node);
    /// <c>false</c> = content present but a DIFFERENT hash — someone else's token
    /// colliding on the 12-char prefix / tampering — a TERMINAL mismatch that must
    /// fail fast, never spin the poll; <c>null</c> = content not (yet) extractable —
    /// transient (mid-create, sync lag), keep polling until the timeout.
    /// </summary>
    private bool? IndexHashVerdict(MeshNode node, string hash)
    {
        var index = node.Content as ApiTokenIndex ?? ExtractApiTokenIndex(node);
        if (index != null)
            return string.Equals(index.TokenHash, hash, StringComparison.OrdinalIgnoreCase);
        var legacy = node.Content as ApiToken ?? ExtractApiToken(node);
        if (legacy != null)
            return string.Equals(legacy.TokenHash, hash, StringComparison.OrdinalIgnoreCase);
        return null;
    }

    /// <inheritdoc cref="IndexHashVerdict"/>
    private bool? TokenHashVerdict(MeshNode node, string hash)
    {
        var token = node.Content as ApiToken ?? ExtractApiToken(node);
        if (token != null)
            return string.Equals(token.TokenHash, hash, StringComparison.OrdinalIgnoreCase);
        return null;
    }

    /// <summary>
    /// Bounded resilient read for the validation path — the exact
    /// <c>OAuthCodeStore.ReadCodeNode</c> shape (#620), with one correctness nuance:
    /// a node that reads SUCCESSFULLY but carries a non-matching hash is a terminal
    /// mismatch (emit null immediately, logged as <c>{stage}-hash-mismatch</c>) —
    /// only read-error/absence re-polls. Each attempt reads through the authoritative
    /// <c>GetMeshNodeStream(path)</c> (activates the owning per-node hub from
    /// Postgres), swallows the transient "No node found", and — ONLY on no-verdict —
    /// re-subscribes after <see cref="ValidationRetryDelay"/> via <c>Concat</c>
    /// (never <c>Merge</c>: exactly one owner-hub subscription live at a time).
    /// Bounded by <see cref="ValidationReadTimeout"/> → Warning
    /// (<c>{stage}-read-timeout</c>) → null. System identity established at
    /// subscribe time (<c>Observable.Using</c>): validation is the entry point that
    /// turns a raw token into an identity, so the caller is by definition
    /// unauthenticated; the hash compare is the actual authentication step.
    /// </summary>
    private IObservable<MeshNode?> ReadValidationNode(
        string path, Func<MeshNode, bool?> hashVerdict, string stage, string hashPrefix, Stopwatch elapsed)
    {
        IObservable<MeshNode?> Attempt() =>
            hub.GetWorkspace().GetMeshNodeStream(path)
                .Take(1)
                .SelectMany(node =>
                {
                    var verdict = node is null ? null : hashVerdict(node);
                    if (verdict == true)
                        return Observable.Return((MeshNode?)node);
                    if (verdict == false)
                        return Observable.Defer(() =>
                        {
                            logger.LogWarning(
                                "API token validation failed at {Stage} for hash prefix {HashPrefix} after {ElapsedMs} ms: node at {Path} exists but carries a different token hash",
                                stage + "-hash-mismatch", hashPrefix, elapsed.ElapsedMilliseconds, path);
                            return Observable.Return((MeshNode?)null);
                        });
                    return Observable.Empty<MeshNode?>();
                })
                .Catch<MeshNode?, Exception>(_ => Observable.Empty<MeshNode?>())
                .Concat(Observable.Defer(Attempt).DelaySubscription(ValidationRetryDelay));

        var poll = Observable.Defer(Attempt)
            .Take(1)
            .Timeout(ValidationReadTimeout)
            .Catch<MeshNode?, Exception>(ex =>
            {
                logger.LogWarning(ex,
                    "API token validation failed at {Stage} for hash prefix {HashPrefix} after {ElapsedMs} ms: node at {Path} not readable within {Timeout}",
                    stage + "-read-timeout", hashPrefix, elapsed.ElapsedMilliseconds, path, ValidationReadTimeout);
                return Observable.Return<MeshNode?>(null);
            });

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return accessService != null
            ? Observable.Using(() => accessService.ImpersonateAsSystem(), _ => poll)
            : poll;
    }

    /// <summary>
    /// Best-effort confirm that a just-created node is readable, tolerating the
    /// create→persist→routable lag. Reads via the authoritative
    /// <c>GetMeshNodeStream(path)</c> — which activates the owning per-node hub from
    /// Postgres — polling one subscription at a time (Concat, never Merge, so a lagging
    /// node never accumulates concurrent owner-hub subscriptions). Each attempt swallows
    /// the transient "No node found" and re-subscribes after 50 ms until the node loads
    /// with content, bounded by a 10 s timeout. On timeout it logs and returns null rather
    /// than failing issuance — the token IS created; worst case validation resolves it a
    /// beat later. System identity: the ApiToken/index nodes are System-owned.
    /// </summary>
    private IObservable<MeshNode?> ConfirmReadable(string path)
    {
        IObservable<MeshNode?> Attempt() =>
            hub.GetWorkspace().GetMeshNodeStream(path)
                .Take(1)
                .Where(n => n is not null && n.Content is not null)
                .Select(n => (MeshNode?)n)
                .Catch<MeshNode?, Exception>(_ => Observable.Empty<MeshNode?>())
                .Concat(Observable.Defer(Attempt).DelaySubscription(TimeSpan.FromMilliseconds(50)));

        var poll = Observable.Defer(Attempt)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .Catch<MeshNode?, Exception>(ex =>
            {
                logger.LogWarning(ex,
                    "API token node {Path} not confirmed readable within 10s of issuance; returning token anyway",
                    path);
                return Observable.Return<MeshNode?>(null);
            });

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return accessService != null
            ? Observable.Using(() => accessService.ImpersonateAsSystem(), _ => poll)
            : poll;
    }

    private ApiToken? FinalizeToken(MeshNode? tokenNode, ApiToken? apiToken, string hash, string hashPrefix, Stopwatch elapsed)
    {
        if (apiToken == null)
            return null; // read failure/mismatch — already logged by ReadValidationNode with its stage
        if (!string.Equals(apiToken.TokenHash, hash, StringComparison.OrdinalIgnoreCase))
        {
            // Defense in depth — the verdict in ReadValidationNode already terminates
            // mismatches, so this branch should be unreachable; log it if it ever fires.
            logger.LogWarning(
                "API token validation failed at {Stage} for hash prefix {HashPrefix} after {ElapsedMs} ms",
                "token-hash-mismatch", hashPrefix, elapsed.ElapsedMilliseconds);
            return null;
        }
        // Warning, not Debug: a rejected token surfaces as a bare 401 to the client —
        // without a server-side line naming the stage, prod triage is blind (the
        // 2026-07-24 fresh-token 401s cost hours because every failure was silent).
        if (apiToken.IsRevoked)
        {
            logger.LogWarning(
                "API token validation failed at {Stage} for hash prefix {HashPrefix} after {ElapsedMs} ms",
                "revoked", hashPrefix, elapsed.ElapsedMilliseconds);
            return null;
        }
        if (apiToken.ExpiresAt.HasValue && apiToken.ExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            logger.LogWarning(
                "API token validation failed at {Stage} for hash prefix {HashPrefix} after {ElapsedMs} ms (expired {ExpiresAt})",
                "expired", hashPrefix, elapsed.ElapsedMilliseconds, apiToken.ExpiresAt.Value);
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
        var now = DateTimeOffset.UtcNow;
        if (tokenNode != null
            && (apiToken.LastUsedAt is null || now - apiToken.LastUsedAt.Value >= LastUsedStampInterval)
            // 🚨 Dispatch-time single-flight: the snapshot check above reads the
            // EVENTUALLY-CONSISTENT query index, and the Update lambda below reads
            // the stream MIRROR — both can lag the stamp that is already in flight,
            // so neither can be the authoritative gate (each lagged validation
            // would re-dispatch a distinct-timestamp patch the owner applies →
            // per-request version bumps, the hot-token storm #210 exists to stop).
            // The memo is dispatch-side state on THIS instance: once one stamp has
            // been dispatched inside the freshness window, every later validation
            // skips regardless of read-side lag.
            && TryClaimStampDispatch(hash, now))
        {
            Interlocked.Increment(ref stampDispatchCount);
            hub.GetWorkspace()
                .GetMeshNodeStream(tokenNode.Path)
                .Update(node =>
                {
                    // Defense-in-depth for MULTI-INSTANCE racing (another silo's stamp
                    // already landed on the node this mirror has since synced): re-check
                    // freshness against the node handed to the lambda and return it
                    // unchanged when a stamp is already recorded — stream.Update
                    // short-circuits an unchanged node into a true no-op (no version
                    // bump, no change-feed fan-out). NOT the primary gate: the mirror
                    // can lag an in-flight stamp, which is what the dispatch-time memo
                    // above closes.
                    var current = node.ContentAs<ApiToken>(hub.JsonSerializerOptions) ?? apiToken;
                    if (current.LastUsedAt is { } last && now - last < LastUsedStampInterval)
                        return node;
                    return node with { Content = current with { LastUsedAt = now } };
                })
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
    /// Atomically claims the right to dispatch a LastUsedAt stamp for
    /// <paramref name="hash"/>: returns <c>true</c> (and records
    /// <paramref name="now"/>) when no stamp has been dispatched by this
    /// instance within <see cref="LastUsedStampInterval"/>; <c>false</c>
    /// when one already has. Lock-free CAS over the instance
    /// <see cref="lastStampDispatchedAt"/> memo — never blocks, never parks
    /// (concurrent-collection compare-exchange, not an async gate).
    /// </summary>
    private bool TryClaimStampDispatch(string hash, DateTimeOffset now)
    {
        while (true)
        {
            if (lastStampDispatchedAt.TryGetValue(hash, out var previous))
            {
                if (now - previous < LastUsedStampInterval)
                    return false; // a stamp for this token is already dispatched/fresh
                if (lastStampDispatchedAt.TryUpdate(hash, now, previous))
                    return true;
                // Lost the CAS to a concurrent claimer — re-read its timestamp.
            }
            else if (lastStampDispatchedAt.TryAdd(hash, now))
            {
                return true;
            }
            // Lost TryAdd to a concurrent claimer — re-read its timestamp.
        }
    }

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
