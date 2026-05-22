using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Security;

/// <summary>
/// Implementation of ISecurityService providing row-level security for mesh nodes.
/// Permissions are derived from AccessAssignment MeshNodes in the node hierarchy.
/// - AccessAssignment MeshNodes: namespace = scope, id = {Subject}_Access, content has Id + Roles[] array
/// - Permission evaluation walks AccessAssignment nodes from root to target path.
/// - Built-in roles: Admin, Editor, Viewer, Commenter.
///
/// IMPORTANT: This service uses the UNSECURED persistence core directly to avoid
/// circular dependency (security checking cannot go through security-filtered persistence).
/// </summary>
internal class SecurityService : ISecurityService, IDisposable
{
    private readonly AccessService _accessService;
    private readonly ILogger<SecurityService> _logger;
    private readonly IMessageHub _hub;

    // Keep-alive Subscribe handles for the two long-standing synced-query
    // streams (AccessAssignment + PartitionAccessPolicy). Held for the
    // service's lifetime so the Replay(1).RefCount caches stay warm —
    // first HasPermission call hits an Initial that's already landed
    // instead of racing the 2 s Timeout in GetEffectivePermissions on a
    // cold subscription. Disposed on service teardown.
    private readonly System.Reactive.Disposables.CompositeDisposable _warmupSubscriptions = new();

    // Built-in roles lookup
    private static readonly Dictionary<string, Role> BuiltInRoles = new()
    {
        { "Admin", Role.Admin },
        { "Editor", Role.Editor },
        { "Viewer", Role.Viewer },
        { "Commenter", Role.Commenter },
        { "PlatformAdmin", Role.PlatformAdmin }
    };

    // Permission lookup keyed by role id — the synchronous fast path used by
    // GetEffectivePermissions when every role in the user's role-set is
    // built-in (the common case). Bypasses GetRole(...).Merge().Aggregate(...)
    // which builds three observables and several closures per call.
    private static readonly IReadOnlyDictionary<string, Permission> BuiltInRolePerms =
        BuiltInRoles.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Permissions, StringComparer.Ordinal);

    // Static policies from IStaticNodeProvider (e.g., Doc, Agent, Role namespaces are read-only)
    private readonly Dictionary<string, PartitionAccessPolicy> _staticPolicies;

    // Static access assignments from IStaticNodeProvider and MeshConfiguration
    // Keyed by namespace (scope), value is list of AccessAssignment nodes at that scope
    private readonly Dictionary<string, List<MeshNode>> _staticAccessAssignments;

    // Per-scope cache of the recursive 4-query union (see
    // Doc/Architecture/AccessControl.md "Effective-assignments lookup").
    // Each cache entry is the CombineLatest of (a) the scope's own
    // ObserveQuery, (b) the parent scope's cached observable (recursion),
    // and — at the entry point — (c) the NodeType chain and (d) the static
    // baselines. Replay(1).RefCount with a keep-alive Subscribe; eviction
    // (sliding 5 min) disposes the keep-alive and lets RefCount tear down
    // the underlying synced query. Cache key is the scope path (string);
    // empty string == root. NodeType and statics are combined at the
    // outer evaluation step, not cached per (scope, nodeType) — that keeps
    // entries shared across consumers regardless of which NodeType they
    // arrived through.
    private readonly IMemoryCache _scopeAssignmentsCache = new MemoryCache(new MemoryCacheOptions());
    private static readonly TimeSpan UserCacheTtl = TimeSpan.FromMinutes(5);

    // Per-scope cache of the recursive policy chain. Same shape as
    // _scopeAssignmentsCache: each entry holds the cumulative
    // namespace→policy map up to that scope, built by recursive
    // CombineLatest with the parent's cached observable. Each scope opens
    // ONE narrow ObserveQuery for `namespace:{scope} id:_Policy
    // nodeType:PartitionAccessPolicy` — at most one matching node per
    // scope, so the query is tiny and Initial emits fast even for empty
    // scopes. See Doc/Architecture/AccessControl.md "Effective-assignments
    // lookup" — policies follow the same shape.
    private readonly IMemoryCache _scopePoliciesCache = new MemoryCache(new MemoryCacheOptions());

    public SecurityService(
        AccessService accessService,
        IMessageHub hub,
        ILogger<SecurityService> logger,
        IEnumerable<IStaticNodeProvider> staticNodeProviders,
        MeshConfiguration? meshConfiguration = null)
    {
        _accessService = accessService;
        _hub = hub;
        _logger = logger;

        var allStaticNodes = staticNodeProviders
            .SelectMany(p => p.GetStaticNodes())
            .ToList();

        // Also include AccessAssignment AND PartitionAccessPolicy nodes from
        // MeshConfiguration (e.g., PublicAdminAccess seeds, partition caps
        // declared via AssignmentNodeFactory.Policy). The previous filter only
        // accepted AccessAssignment, so PartitionAccessPolicy seeds added via
        // AddMeshNodes were silently dropped — caps never applied, Admin saw
        // Permission.All on every scope.
        if (meshConfiguration != null)
        {
            allStaticNodes.AddRange(
                meshConfiguration.Nodes.Values
                    .Where(n => n.NodeType == "AccessAssignment"
                                || n.NodeType == "PartitionAccessPolicy"));
        }

        // Collect PartitionAccessPolicy nodes from static providers (last-wins for duplicate namespaces)
        _staticPolicies = allStaticNodes
            .Where(n => n.NodeType == "PartitionAccessPolicy" && n.Id == "_Policy" && n.Content is PartitionAccessPolicy)
            .GroupBy(n => n.Namespace ?? "")
            .ToDictionary(g => g.Key, g => (PartitionAccessPolicy)g.Last().Content!);

        // Collect AccessAssignment nodes keyed by their parent namespace (scope)
        _staticAccessAssignments = allStaticNodes
            .Where(n => n.NodeType == "AccessAssignment" && n.Content != null)
            .GroupBy(n => n.Namespace ?? "")
            .ToDictionary(g => g.Key, g => g.ToList());

        // Eager-subscribe the ROOT scope's effective-assignments observable
        // plus the PartitionAccessPolicy synced query so the
        // Replay(1).RefCount caches are populated by the time the first
        // HasPermission / GetEffectivePermissions call arrives. Without
        // this, the first caller activates the upstream subscription cold —
        // historically that races a Timeout in GetEffectivePermissions
        // (now removed) and falls back to "no synced roles".
        //
        // The root scope warm-up cascades: every other scope's cache entry
        // recurses through the parent chain to the root, which is already
        // warm and emits its Initial synchronously from the
        // Replay(1).RefCount snapshot. Statics fold in at the same step.
        try
        {
            _warmupSubscriptions.Add(ObserveScopeAssignments("")
                .Subscribe(_ => { },
                    ex => _logger.LogWarning(ex,
                        "SecurityService warm-up: root scope AccessAssignment subscription faulted")));
            _warmupSubscriptions.Add(ObserveScopePolicies("")
                .Subscribe(_ => { },
                    ex => _logger.LogWarning(ex,
                        "SecurityService warm-up: root scope PartitionAccessPolicy subscription faulted")));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "SecurityService warm-up: failed to subscribe at construction — falling back to lazy init");
        }
    }

    public void Dispose()
    {
        _warmupSubscriptions.Dispose();
        if (_scopeAssignmentsCache is IDisposable disposableAssignments)
            disposableAssignments.Dispose();
        if (_scopePoliciesCache is IDisposable disposablePolicies)
            disposablePolicies.Dispose();
    }

    private JsonSerializerOptions Options => _hub.JsonSerializerOptions;

    #region Permission Evaluation

    public IObservable<bool> HasPermission(string nodePath, Permission permission)
    {
        var context = _accessService.Context ?? _accessService.CircuitContext;
        var userId = context?.ObjectId;
        if (string.IsNullOrEmpty(userId) || context?.IsVirtual == true)
            userId = WellKnownUsers.Anonymous;
        return HasPermission(nodePath, userId, permission);
    }

    public IObservable<bool> HasPermission(string nodePath, string userId, Permission permission)
    {
        if (permission == Permission.None)
            return Observable.Return(true);
        return GetEffectivePermissions(nodePath, userId)
            .Select(p => p.HasFlag(permission));
    }

    public IObservable<Permission> GetEffectivePermissions(string nodePath)
    {
        var context = _accessService.Context ?? _accessService.CircuitContext;
        var userId = context?.ObjectId;
        if (string.IsNullOrEmpty(userId) || context?.IsVirtual == true)
            userId = WellKnownUsers.Anonymous;
        return GetEffectivePermissions(nodePath, userId);
    }

    public IObservable<Permission> GetEffectivePermissions(string nodePath, string userId)
    {
        if (string.IsNullOrEmpty(userId))
            userId = WellKnownUsers.Anonymous;

        // System identity has full access.
        if (userId == WellKnownUsers.System)
            return Observable.Return(Permission.All);

        // 🚨 Sanctioned dedicated identity: MeshNodeStreamCache's hydrator
        // (Doc/Architecture/AccessContextPropagation.md → "Sanctioned exceptions").
        // The cache subscribes to per-path streams under this identity at
        // startup — it needs Permission.Read to receive the upstream snapshot
        // and updates. It MUST NOT have Create / Update / Delete; writes under
        // this identity are denied here by virtue of returning ONLY Read.
        // Tests at MeshWeaver.Security.Test.MeshNodeCacheIdentityTest verify
        // the boundary — writes fail with UnauthorizedAccessException.
        if (userId == MeshNodeCacheIdentity.Address)
            return Observable.Return(Permission.Read);

        // Claim-first composition: the claim-based AccessContext.Roles + the
        // static AccessAssignment seeds collected at construction time are
        // available SYNCHRONOUSLY — no observable wait, no synced-query
        // dependency. Compute that snapshot, emit it as the first value, THEN
        // enrich asynchronously with the synced AccessAssignment query (so
        // long-lived subscribers see updates as runtime grants land).
        //
        // Why claim-first: API-token requests carry their full role set on
        // the bearer token (validated, signed). That's the trustworthy answer
        // — permissions resolve in microseconds without ever touching the
        // mesh-query layer. AccessControlPipeline does Take(1), so it gets
        // this fast answer; if you need live updates (Blazor side panels
        // rendering "current permissions") you keep the subscription open
        // and receive the post-synced enriched value seamlessly.
        //
        // Why this beats the previous Timeout-then-Catch shape: that one
        // still blocked for up to 2 s on every check waiting for the synced
        // stream to produce its first emission. With claim-first emission,
        // the worst case for an authorised request is sub-millisecond; the
        // synced query is enrichment, not a gate.
        var staticOnlyScopeRoles = ComputeStaticOnlyScopeRoles(userId);
        var staticOnlyDeniedScopeRoles = ComputeStaticOnlyDeniedScopeRoles(userId);
        var fast = ComputeRoleState(staticOnlyScopeRoles, nodePath, userId, staticOnlyDeniedScopeRoles);

        // 4-query union per scope: see Doc/Architecture/AccessControl.md
        // "Effective-assignments lookup". The per-scope cache builds the
        // effective AccessAssignment set as a recursive CombineLatest:
        //   EffectiveAt(a/b/c) = EffectiveAt(a/b) ∪ SelfAt(a/b/c)
        // bottoming out at the root which also unions in static baselines.
        // Each scope opens ONE narrow ObserveQuery (`namespace:{scope}/_Access`)
        // that emits Initial as soon as the engine resolves the namespace —
        // empty result is a first-class emission, no Timeout needed.
        //
        // CombineLatest with ObserveAllPolicies so a runtime PartitionAccessPolicy
        // participates in the cap/inheritance walk. No Timeout/Catch fallback:
        // the narrow per-scope queries don't suffer the cold-start lag that the
        // old global `scope:subtree` query did, so there's no warm-up window
        // worth guarding against. If the synced engine genuinely faults, the
        // error propagates through to the caller (AccessControlPipeline) which
        // has its own 10 s deadline and fails closed.
        var enriched = ObserveEffectiveAssignments(nodePath)
            .CombineLatest(
                // Pass nodePath here too: the policy chain we need is the one
                // ending at this scope. Sibling paths share their common
                // ancestor's cached entries via the recursive parent chain.
                ObserveScopePolicies(nodePath),
                (nodes, policies) =>
                {
                    var (granted, denied) = ComputeScopeRoles(userId, nodes);
                    return (Granted: granted, Denied: denied, RuntimePolicies: policies);
                })
            .Select(snap => ComputeRoleState(snap.Granted, nodePath, userId, snap.Denied, snap.RuntimePolicies));

        // Only emit the fast snapshot when it actually grants something —
        // otherwise FirstAsync() callers (AccessControlPipeline does Take(1))
        // would lock in the empty answer before runtime AccessAssignments
        // visible only via the synced query had a chance to land. The
        // polling pattern in tests + UI layouts relies on the next emission
        // arriving when a freshly-created assignment is observed.
        var seed = fast.RoleIds.Count > 0
            ? Observable.Return(fast)
            : Observable.Empty<(ImmutableHashSet<string>, Permission)>();

        return seed.Concat(enriched)
            .SelectMany(state =>
            {
                var (roleIds, permissionCap) = state;

                // Fast path: every role is built-in → resolve synchronously
                // from the BuiltInRolePerms lookup, no per-role observable
                // composition. This is the common case (tests + most prod
                // tenants don't use custom Role definitions) and avoids
                // building Merge + Aggregate + Where observables.
                Permission rolePermsValue = Permission.None;
                ImmutableHashSet<string>? customRoleIds = null;
                foreach (var rid in roleIds)
                {
                    if (BuiltInRolePerms.TryGetValue(rid, out var p))
                        rolePermsValue |= p;
                    else
                        customRoleIds = (customRoleIds ?? ImmutableHashSet<string>.Empty).Add(rid);
                }

                IObservable<Permission> rolePerms = customRoleIds is null
                    ? Observable.Return(rolePermsValue)
                    : Observable.Return(rolePermsValue).CombineLatest(
                        customRoleIds
                            .Select(GetRole)
                            .Merge()
                            .Where(r => r is not null)
                            .Aggregate(Permission.None, (acc, r) => acc | r!.Permissions),
                        (builtIn, custom) => builtIn | custom);

                IObservable<Permission> withPublic = (userId != WellKnownUsers.Anonymous && userId != WellKnownUsers.Public)
                    ? rolePerms.Zip(GetEffectivePermissions(nodePath, WellKnownUsers.Public),
                        (own, pub) => own | pub)
                    : rolePerms;

                return withPublic.Select(p =>
                {
                    p &= permissionCap;
                    var currentContext = _accessService.Context ?? _accessService.CircuitContext;
                    if (currentContext?.IsApiToken == true && !p.HasFlag(Permission.Api))
                        p = Permission.None;
                    _logger.LogTrace("User {UserId} has permissions {Permissions} on node {NodePath} (cap: {Cap})",
                        userId, p, nodePath, permissionCap);
                    return p;
                });
            })
            .DistinctUntilChanged();
    }

    /// <summary>
    /// Synchronously walks the static <see cref="_staticAccessAssignments"/>
    /// (collected at construction from <see cref="IStaticNodeProvider"/>s and
    /// <see cref="MeshConfiguration.Nodes"/>) and produces a <c>scope → roles</c>
    /// map for <paramref name="userId"/>. Used for the immediate emission in
    /// <see cref="GetEffectivePermissions"/> so an authenticated request with a
    /// static AccessAssignment grant doesn't have to wait for the synced query
    /// to settle.
    /// </summary>
    private ImmutableDictionary<string, ImmutableHashSet<string>> ComputeStaticOnlyScopeRoles(string userId)
    {
        var result = ImmutableDictionary<string, ImmutableHashSet<string>>.Empty;
        foreach (var (_, list) in _staticAccessAssignments)
        {
            foreach (var node in list)
            {
                if (node.NodeType != SecurityCollections.AccessAssignmentNodeType)
                    continue;
                var ns = node.Namespace ?? "";
                var scope = ns.EndsWith("/_Access", StringComparison.Ordinal)
                    ? ns[..^"/_Access".Length]
                    : (ns == "_Access" ? "" : null);
                if (scope is null)
                    continue;
                var assignment = DeserializeAssignment(node);
                if (assignment == null || assignment.AccessObject != userId)
                    continue;
                foreach (var ra in assignment.Roles)
                {
                    if (string.IsNullOrEmpty(ra.Role) || ra.Denied)
                        continue;
                    var existing = result.TryGetValue(scope, out var roles)
                        ? roles
                        : ImmutableHashSet<string>.Empty;
                    result = result.SetItem(scope, existing.Add(ra.Role));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Mirror of <see cref="ComputeStaticOnlyScopeRoles"/> for the OPPOSITE
    /// half: a <c>scope → denied-roles</c> map for the user, drawn from
    /// every <c>RoleAssignment</c> with <c>Denied = true</c>. Walks the same
    /// static seeds the granted half walks, so a deny seeded at construction
    /// time takes effect on the immediate claim-only emission alongside the
    /// granted role state.
    /// </summary>
    private ImmutableDictionary<string, ImmutableHashSet<string>> ComputeStaticOnlyDeniedScopeRoles(string userId)
    {
        var result = ImmutableDictionary<string, ImmutableHashSet<string>>.Empty;
        foreach (var (_, list) in _staticAccessAssignments)
        {
            foreach (var node in list)
            {
                if (node.NodeType != SecurityCollections.AccessAssignmentNodeType)
                    continue;
                var ns = node.Namespace ?? "";
                var scope = ns.EndsWith("/_Access", StringComparison.Ordinal)
                    ? ns[..^"/_Access".Length]
                    : (ns == "_Access" ? "" : null);
                if (scope is null)
                    continue;
                var assignment = DeserializeAssignment(node);
                if (assignment == null || assignment.AccessObject != userId)
                    continue;
                foreach (var ra in assignment.Roles)
                {
                    if (string.IsNullOrEmpty(ra.Role) || !ra.Denied)
                        continue;
                    var existing = result.TryGetValue(scope, out var roles)
                        ? roles
                        : ImmutableHashSet<string>.Empty;
                    result = result.SetItem(scope, existing.Add(ra.Role));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Folds the synced scope-to-roles snapshot (possibly empty) with the
    /// claim-based <see cref="AccessContext.Roles"/> for <paramref name="userId"/>
    /// and the static seeds collected at construction time. Pure synchronous —
    /// no observable, no I/O — so the caller can use it both for the immediate
    /// claim-only emission and for the post-synced enriched emission.
    /// </summary>
    private (ImmutableHashSet<string> RoleIds, Permission PermissionCap) ComputeRoleState(
        ImmutableDictionary<string, ImmutableHashSet<string>> scopeToRoles,
        string nodePath,
        string userId,
        ImmutableDictionary<string, ImmutableHashSet<string>>? scopeToDeniedRoles = null,
        ImmutableDictionary<string, PartitionAccessPolicy>? runtimePolicies = null)
    {
        var roleIds = ImmutableHashSet<string>.Empty;
        var permissionCap = Permission.All;
        var isSelfScopeOwner = userId != WellKnownUsers.Anonymous
                               && userId != WellKnownUsers.Public;
        foreach (var scope in GetScopeHierarchy(nodePath))
        {
            // Merge static + runtime policy at this scope. Runtime overrides
            // static when both exist (test seeds via AssignmentNodeFactory.Policy
            // do a runtime CreateNode that should beat any earlier static seed
            // at the same namespace).
            PartitionAccessPolicy? policy = null;
            if (runtimePolicies is not null && runtimePolicies.TryGetValue(scope, out var rp))
                policy = rp;
            else if (_staticPolicies.TryGetValue(scope, out var sp))
                policy = sp;

            // BreaksInheritance: the policy at this scope discards everything
            // that was inherited from ancestors — both the role grants AND any
            // partition-level cap that was being narrowed on the way down.
            // Local roles defined AT this scope (and below) still apply, so
            // we drop accumulated state BEFORE this iteration's contributions
            // land. The flag is set in addition to the per-permission switches,
            // so the same policy can still cap the local roles via the
            // GetPermissionCap call further down.
            if (policy is not null && policy.BreaksInheritance)
            {
                roleIds = ImmutableHashSet<string>.Empty;
                permissionCap = Permission.All;
            }

            if (scopeToRoles.TryGetValue(scope, out var roles))
                roleIds = roleIds.Union(roles);
            if (policy is not null)
                permissionCap &= policy.GetPermissionCap();

            // Apply role-level denies at this scope: an AccessAssignment with
            // Denied=true subtracts the listed roles from the running union.
            // This is per-scope (not aggregated up front) so a deny at a
            // descendant only kicks in once the walk reaches that scope.
            if (scopeToDeniedRoles is not null
                && scopeToDeniedRoles.TryGetValue(scope, out var deniedRoles))
                roleIds = roleIds.Except(deniedRoles);

            // Self-scope: a user implicitly holds the Admin role on their own
            // partition (path == "{userId}" or under "{userId}/"). The role
            // composes through the same BuiltInRolePerms + PartitionAccessPolicy
            // pipeline as any other grant, so a static policy capping the
            // partition still applies — we don't bypass the role/cap chain.
            if (isSelfScopeOwner
                && string.Equals(scope, userId, StringComparison.OrdinalIgnoreCase))
                roleIds = roleIds.Add(Role.Admin.Id);
        }

        // Claim-based roles: stamped by the auth pipeline onto the
        // AccessContext (Bearer-token Roles claims, OAuth role claims, or
        // tests that SetContext directly). Trusted as-is — they're either
        // signed (Bearer) or the result of an explicit middleware-driven
        // resolution (OAuth → DB → AccessContext).
        //
        // 🚨 Check BOTH Context (per-request AsyncLocal, may hold stale
        // hub-init "system-security") AND CircuitContext (per-circuit
        // persistent identity). The match condition `ObjectId == userId`
        // filters out any context that doesn't represent THIS user — so
        // hub-init's system-security AsyncLocal is harmlessly skipped when
        // resolving a non-System user's roles. Without checking both, a
        // request's claim-Roles can't be found if the AsyncLocal Context
        // is contaminated by hub-init impersonations.
        AddClaimRoles(_accessService.Context);
        AddClaimRoles(_accessService.CircuitContext);

        void AddClaimRoles(AccessContext? ctx)
        {
            if (ctx?.Roles != null
                && !string.IsNullOrEmpty(ctx.ObjectId)
                && ctx.ObjectId == userId)
            {
                foreach (var roleName in ctx.Roles)
                    roleIds = roleIds.Add(roleName);
            }
        }

        return (roleIds, permissionCap);
    }

    /// <summary>
    /// Per-scope effective AccessAssignment observable. Implements the
    /// recursive 4-query union described in <c>Doc/Architecture/AccessControl.md</c>:
    /// <list type="bullet">
    ///   <item><c>EffectiveAt(scope) = EffectiveAt(parent(scope)) ∪ SelfAt(scope)</c>
    ///     — every level loads only its own <c>_Access</c> namespace; the
    ///     combo with the level above is via <c>CombineLatest</c>.</item>
    ///   <item><c>EffectiveAt("") = SelfAt("") ∪ statics</c> — root scope
    ///     folds in <see cref="_staticAccessAssignments"/> from
    ///     <c>IStaticNodeProvider</c>.</item>
    /// </list>
    ///
    /// <para>Each cache entry is a <c>Replay(1).RefCount</c> backed by a
    /// keep-alive Subscribe. Eviction (sliding 5 min) disposes the
    /// keep-alive and lets the underlying narrow <c>ObserveQuery</c>
    /// tear down via RefCount. Empty Initial is a first-class emission —
    /// most scopes hold zero AccessAssignment rows, the query resolves
    /// synchronously to <c>Empty</c>, and the chain propagates without
    /// any Timeout fallback.</para>
    ///
    /// <para>Recursion termination: <paramref name="scope"/> == ""
    /// (the root) does not recurse; it folds in <see cref="_staticAccessAssignments"/>
    /// instead.</para>
    /// </summary>
    private IObservable<IEnumerable<MeshNode>> ObserveScopeAssignments(string scope)
    {
        var key = scope ?? string.Empty;
        return _scopeAssignmentsCache.GetOrCreate(key, entry =>
        {
            entry.SlidingExpiration = UserCacheTtl;

            // Self: narrow ObserveQuery for THIS scope's _Access subtree only.
            // No `scope:selfAndAncestors` — every level loads itself; the chain
            // is built via the parent CombineLatest below.
            //
            // 🚨 AccessAssignment lookup MUST run as System — otherwise we
            // recurse: SecurityService.HasPermission(path, alice, Read)
            // → workspace.GetQuery (per-user cache, key=…@alice)
            // → SyncedQueryMeshNodes opens secured query for alice
            // → secured provider applies validators per AccessAssignment node
            // → validators call SecurityService.HasPermission again
            // → cycle / hang. The ImpersonateAsSystem wrap pins the cache key
            //   under "system-security" so all security queries share one
            //   infrastructure cache and the unsecured (validator-bypassing)
            //   provider surface lights up.
            var workspace = _hub.GetWorkspace();
            var accessSvc = _hub.ServiceProvider.GetService<AccessService>();
            var nsQuery = string.IsNullOrEmpty(key) ? "_Access" : $"{key}/_Access";
            IObservable<IEnumerable<MeshNode>> self;
            using (accessSvc?.ImpersonateAsSystem())
            {
                self = workspace.GetQuery(
                    $"$security-access:{key}",
                    $"namespace:{nsQuery} nodeType:{SecurityCollections.AccessAssignmentNodeType}");
            }

            // Parent: recursive reference to the parent scope's cached
            // observable. Root scope folds in statics instead.
            IObservable<IEnumerable<MeshNode>> parentOrBase;
            if (string.IsNullOrEmpty(key))
            {
                // Static baselines from IStaticNodeProvider — synchronous,
                // emitted once. Captured at construction time into
                // _staticAccessAssignments (already grouped by namespace).
                var staticNodes = _staticAccessAssignments.Values
                    .SelectMany(list => list)
                    .Where(n => n.NodeType == SecurityCollections.AccessAssignmentNodeType)
                    .ToArray();
                parentOrBase = Observable.Return<IEnumerable<MeshNode>>(staticNodes);
            }
            else
            {
                var parentScope = GetParentScope(key);
                parentOrBase = ObserveScopeAssignments(parentScope);
            }

            var combined = Observable.CombineLatest(self, parentOrBase, UnionByPath)
                .DistinctUntilChanged(MeshNodeListPathEquality.Instance)
                .Replay(1)
                .RefCount();

            var keepAlive = combined.Subscribe(_ => { }, _ => { });
            entry.RegisterPostEvictionCallback((_, _, _, _) => keepAlive.Dispose());
            return combined;
        })!;
    }

    /// <summary>
    /// Entry point combining the scope chain with the (optional) NodeType
    /// chain. Both chains are independent recursions through
    /// <see cref="ObserveScopeAssignments"/>; the NodeType chain captures
    /// AccessAssignments living at the NodeType's own path (e.g.
    /// <c>Agent/_Access</c>) that apply to every instance of that NodeType.
    /// </summary>
    private IObservable<IEnumerable<MeshNode>> ObserveEffectiveAssignments(
        string nodePath, string? nodeTypePath = null)
    {
        var scopeChain = ObserveScopeAssignments(nodePath ?? string.Empty);
        if (string.IsNullOrEmpty(nodeTypePath))
            return scopeChain;
        var typeChain = ObserveScopeAssignments(nodeTypePath);
        return Observable.CombineLatest(scopeChain, typeChain, UnionByPath);
    }

    /// <summary>
    /// Splits <paramref name="scope"/> at the last <c>/</c> to produce the
    /// parent path. Root and single-segment scopes return the root ("").
    /// </summary>
    private static string GetParentScope(string scope)
    {
        if (string.IsNullOrEmpty(scope)) return string.Empty;
        var idx = scope.LastIndexOf('/');
        return idx < 0 ? string.Empty : scope[..idx];
    }

    /// <summary>
    /// Union of two MeshNode sequences, deduplicated by <see cref="MeshNode.Path"/>.
    /// Used by the recursive 4-query union to combine self/parent at every
    /// scope level and scope/NodeType at the outer evaluation step.
    /// </summary>
    private static IEnumerable<MeshNode> UnionByPath(
        IEnumerable<MeshNode> first, IEnumerable<MeshNode> second)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MeshNode>();
        foreach (var n in first)
            if (!string.IsNullOrEmpty(n.Path) && seen.Add(n.Path))
                result.Add(n);
        foreach (var n in second)
            if (!string.IsNullOrEmpty(n.Path) && seen.Add(n.Path))
                result.Add(n);
        return result;
    }

    /// <summary>
    /// Equality by the unioned set of MeshNode paths — used by
    /// <c>DistinctUntilChanged</c> on the recursive CombineLatest so we
    /// don't re-emit when both upstreams fire with identical content.
    /// </summary>
    private sealed class MeshNodeListPathEquality : IEqualityComparer<IEnumerable<MeshNode>>
    {
        public static readonly MeshNodeListPathEquality Instance = new();

        public bool Equals(IEnumerable<MeshNode>? x, IEnumerable<MeshNode>? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            var xs = x.Select(n => n.Path).Where(p => !string.IsNullOrEmpty(p)).ToHashSet(StringComparer.Ordinal);
            var ys = y.Select(n => n.Path).Where(p => !string.IsNullOrEmpty(p)).ToHashSet(StringComparer.Ordinal);
            return xs.SetEquals(ys);
        }

        public int GetHashCode(IEnumerable<MeshNode> obj) => obj.Count();
    }

    private (
        ImmutableDictionary<string, ImmutableHashSet<string>> Granted,
        ImmutableDictionary<string, ImmutableHashSet<string>> Denied) ComputeScopeRoles(
        string userId, IEnumerable<MeshNode> allNodes)
    {
        var granted = ImmutableDictionary<string, ImmutableHashSet<string>>.Empty;
        var denied = ImmutableDictionary<string, ImmutableHashSet<string>>.Empty;

        void Consume(MeshNode node)
        {
            if (node.NodeType != SecurityCollections.AccessAssignmentNodeType)
                return;
            var ns = node.Namespace ?? "";
            var scope = ns.EndsWith("/_Access", StringComparison.Ordinal)
                ? ns[..^"/_Access".Length]
                : (ns == "_Access" ? "" : null);
            if (scope is null)
                return;

            var assignment = DeserializeAssignment(node);
            if (assignment == null || assignment.AccessObject != userId)
                return;

            foreach (var ra in assignment.Roles)
            {
                if (string.IsNullOrEmpty(ra.Role))
                    continue;
                var target = ra.Denied ? denied : granted;
                var existing = target.TryGetValue(scope, out var roles)
                    ? roles
                    : ImmutableHashSet<string>.Empty;
                if (ra.Denied)
                    denied = denied.SetItem(scope, existing.Add(ra.Role));
                else
                    granted = granted.SetItem(scope, existing.Add(ra.Role));
            }
        }

        foreach (var node in allNodes)
            Consume(node);
        foreach (var (_, list) in _staticAccessAssignments)
            foreach (var node in list)
                Consume(node);

        return (Granted: granted, Denied: denied);
    }

    private const string AccessAssignmentQueryId = "$security-access-assignments";
    private const string RoleQueryId = "$security-roles";

    /// <summary>
    /// Live AccessAssignment <see cref="MeshNode"/> collection backed by the
    /// workspace's synced mesh-query (auto-registered on first read via
    /// <see cref="SyncedQueryDataSourceExtensions.GetQuery(IWorkspace, object, string)"/>).
    /// Hot, replayed observable — every subscriber sees the current snapshot
    /// on subscribe and every subsequent <c>Initial</c> / <c>Added</c> /
    /// <c>Updated</c> / <c>Removed</c> delta as the underlying query
    /// result-set evolves. Static seeds folded in separately via
    /// <c>_staticAccessAssignments</c>.
    ///
    /// <para>The recursion that previously made this dangerous (synced query
    /// → IMeshQueryProvider → StorageAdapterMeshQueryProvider's QueryAsync → RlsNodeValidator
    /// → SecurityService → here) is now broken at the validator: SyncedQueryMeshNodes
    /// runs the upstream <see cref="IMeshQueryProvider.ObserveQuery"/> call
    /// with <see cref="WellKnownUsers.System"/>, and
    /// <c>RlsNodeValidator.Validate</c> short-circuits to <c>Valid</c> for
    /// that identity. No re-entrant call back into this method.</para>
    /// </summary>
    /// <summary>
    /// Live <see cref="PartitionAccessPolicy"/> map keyed by namespace, drawn
    /// from the same workspace synced-query mechanism that backs the
    /// AccessAssignment surface. Required so a runtime
    /// <see cref="AssignmentNodeFactory.Policy"/> create participates in the
    /// scope-walk in <see cref="ComputeRoleState"/> alongside the static seeds
    /// loaded into <c>_staticPolicies</c>.
    /// </summary>
    /// <summary>
    /// Per-scope cached policy chain. Mirrors <see cref="ObserveScopeAssignments"/>:
    /// each cache entry runs a narrow <c>ObserveQuery</c> for ONLY this scope's
    /// <c>_Policy</c> node and unions it with the parent scope's cached chain
    /// via <c>CombineLatest</c>. The emission is the cumulative
    /// <c>namespace → policy</c> map from root to this scope. Recursion bottoms
    /// out at the root which folds in <see cref="_staticPolicies"/> from
    /// <see cref="IStaticNodeProvider"/>s.
    ///
    /// <para><c>EffectivePolicies(a/b/c) = EffectivePolicies(a/b) ∪
    /// SelfPolicy(a/b/c)</c> — keyed by namespace. The cap/inheritance walk
    /// in <see cref="ComputeRoleState"/> looks each scope's policy up in the
    /// map; <c>BreaksInheritance</c> applied at the walk-time, not at the
    /// composition step.</para>
    /// </summary>
    private IObservable<ImmutableDictionary<string, PartitionAccessPolicy>>
        ObserveScopePolicies(string scope)
    {
        var key = scope ?? string.Empty;
        return _scopePoliciesCache.GetOrCreate(key, entry =>
        {
            entry.SlidingExpiration = UserCacheTtl;

            // Self: narrow query for THIS scope's _Policy node. At most one
            // match per scope; Initial emits Empty fast for scopes without
            // a runtime policy. No `scope:selfAndAncestors` — each level
            // loads itself; the chain is built via the parent CombineLatest.
            var workspace = _hub.GetWorkspace();
            var accessSvc = _hub.ServiceProvider.GetService<AccessService>();
            var nsFilter = string.IsNullOrEmpty(key)
                ? "namespace: id:_Policy"
                : $"namespace:{key} id:_Policy";
            // Same System-pin as ObserveScopeAssignments — policy lookup is
            // infrastructure; the secured surface would recurse via validators.
            IObservable<IEnumerable<MeshNode>> self;
            using (accessSvc?.ImpersonateAsSystem())
            {
                self = workspace.GetQuery(
                    $"$security-policy:{key}",
                    $"{nsFilter} nodeType:{SecurityCollections.PartitionAccessPolicyNodeType}");
            }

            // Parent: recursive reference to parent scope's cached policy map.
            // Root scope folds in statics instead.
            IObservable<ImmutableDictionary<string, PartitionAccessPolicy>> parentOrBase;
            if (string.IsNullOrEmpty(key))
            {
                var staticMap = _staticPolicies.Aggregate(
                    ImmutableDictionary<string, PartitionAccessPolicy>.Empty,
                    (acc, kvp) => acc.SetItem(kvp.Key, kvp.Value));
                parentOrBase = Observable.Return(staticMap);
            }
            else
            {
                parentOrBase = ObserveScopePolicies(GetParentScope(key));
            }

            var combined = Observable.CombineLatest(self, parentOrBase,
                (selfNodes, parentMap) =>
                {
                    var dict = parentMap;
                    foreach (var node in selfNodes)
                    {
                        if (node.Id != "_Policy") continue;
                        var policy = node.Content as PartitionAccessPolicy
                                     ?? DeserializePolicy(node);
                        if (policy is null) continue;
                        dict = dict.SetItem(node.Namespace ?? string.Empty, policy);
                    }
                    return dict;
                })
                .DistinctUntilChanged()
                .Replay(1)
                .RefCount();

            var keepAlive = combined.Subscribe(_ => { }, _ => { });
            entry.RegisterPostEvictionCallback((_, _, _, _) => keepAlive.Dispose());
            return combined;
        })!;
    }

    /// <summary>
    /// Back-compat shim: callers that want "every policy in the mesh" now
    /// resolve the cumulative map at the root of the scope tree the call
    /// site is interested in. The previous singleton <c>ObserveAllPolicies</c>
    /// returned a full <c>scope:subtree</c> map; with per-scope queries we
    /// can't produce that without walking every scope, so this entry point
    /// returns the root-and-statics map. Permission evaluation already
    /// looks policies up by walking the path's scope hierarchy in
    /// <see cref="ComputeRoleState"/> — feed the per-path policy chain via
    /// <see cref="ObserveScopePolicies"/> instead of this back-compat surface
    /// wherever the path is known.
    /// </summary>
    private IObservable<ImmutableDictionary<string, PartitionAccessPolicy>> ObserveAllPolicies()
        => ObserveScopePolicies(string.Empty);

    // ObserveAllMeshNodes removed — replaced by per-scope recursive union
    // in ObserveScopeAssignments. The old global `scope:subtree` query
    // returned every AccessAssignment in the mesh, regardless of relevance
    // to the path being checked; the new design uses one narrow query per
    // scope level and unions via CombineLatest. See
    // Doc/Architecture/AccessControl.md "Effective-assignments lookup".

    /// <summary>
    /// Live <see cref="MeshNode"/> collection for every Role definition in the
    /// mesh — both the built-in seeds from <see cref="RoleNodeType.AddRoleType"/>'s
    /// <c>BuiltInRolesProvider</c> AND any custom Role nodes created at runtime.
    /// Backed by the same workspace-scoped synced-query cache as the
    /// AccessAssignment surface; runs with <see cref="WellKnownUsers.System"/>
    /// identity (so the Role namespace's read-validators don't recurse back here).
    /// Built-in roles still have a fast in-memory dictionary path
    /// (<see cref="BuiltInRoles"/>) for the hot permission-evaluation loop —
    /// the synced query is consulted only for non-built-in role IDs.
    /// </summary>
    private IObservable<MeshNode[]> ObserveAllRoleNodes()
    {
        var workspace = _hub.GetWorkspace();
        return workspace
            .GetQuery(RoleQueryId,
                $"nodeType:{RoleNodeType.NodeType} scope:subtree")
            .Select(arr => arr.ToArray());
    }

    private Role? DeserializeRole(MeshNode node)
    {
        if (node.Content is Role r)
            return r;
        if (node.Content is JsonElement je)
        {
            try
            {
                return JsonSerializer.Deserialize<Role>(je.GetRawText(), Options);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Walks the scope hierarchy and gathers every role ID assigned to
    /// <paramref name="userId"/> in the in-memory static collections, plus the
    /// accumulated permission cap from static policies. Synchronous over
    /// in-memory state — no I/O. Dynamic assignments need synced collections
    /// (separate refactor).
    /// </summary>
    private (System.Collections.Immutable.ImmutableHashSet<string> RoleIds, Permission Cap)
        CollectStaticRoleIds(string nodePath, string userId)
    {
        var roleIds = System.Collections.Immutable.ImmutableHashSet<string>.Empty;
        var cap = Permission.All;

        foreach (var scope in GetScopeHierarchy(nodePath))
        {
            if (_staticPolicies.TryGetValue(scope, out var staticPolicy))
            {
                if (staticPolicy.BreaksInheritance)
                    roleIds = System.Collections.Immutable.ImmutableHashSet<string>.Empty;
                cap &= staticPolicy.GetPermissionCap();
            }

            if (_staticAccessAssignments.TryGetValue(scope, out var staticAssignmentNodes))
            {
                foreach (var staticNode in staticAssignmentNodes)
                {
                    var staticAssignment = DeserializeAssignment(staticNode);
                    if (staticAssignment == null || staticAssignment.AccessObject != userId)
                        continue;
                    foreach (var ra in staticAssignment.Roles)
                    {
                        if (string.IsNullOrEmpty(ra.Role) || ra.Denied)
                            continue;
                        roleIds = roleIds.Add(ra.Role);
                    }
                }
            }
        }
        return (roleIds, cap);
    }

    /// <summary>
    /// Returns the scope hierarchy for permission evaluation.
    /// Order: global (""), then root, then each level down to the target path.
    /// E.g., "ACME/Project/Task1" -> ["", "ACME", "ACME/Project", "ACME/Project/Task1"]
    /// </summary>
    private static List<string> GetScopeHierarchy(string nodePath)
    {
        var scopes = new List<string> { "" }; // global scope

        if (!string.IsNullOrEmpty(nodePath))
        {
            var segments = nodePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i <= segments.Length; i++)
            {
                scopes.Add(string.Join("/", segments.Take(i)));
            }
        }

        return scopes;
    }

    // PlatformAdmin scope check is now folded into ComputeEffectivePermissions
    // via the static-assignments scan; persistence walks are gone.

    private AccessAssignment? DeserializeAssignment(MeshNode node)
    {
        if (node.Content is AccessAssignment aa)
            return aa;
        if (node.Content is System.Text.Json.JsonElement je)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<AccessAssignment>(je.GetRawText(), Options);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    private PartitionAccessPolicy? DeserializePolicy(MeshNode node)
    {
        if (node.Content is PartitionAccessPolicy policy)
            return policy;
        if (node.Content is System.Text.Json.JsonElement je)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<PartitionAccessPolicy>(je.GetRawText(), Options);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    #endregion

    #region Role Definitions

    public IObservable<Role?> GetRole(string roleId)
    {
        if (string.IsNullOrEmpty(roleId))
            return Observable.Return<Role?>(null);
        if (BuiltInRoles.TryGetValue(roleId, out var builtIn))
            return Observable.Return<Role?>(builtIn);

        // Snapshot lookup of the synced Role collection — Take(1) so the
        // Aggregate downstream in GetEffectivePermissions actually completes.
        return ObserveAllRoleNodes()
            .Take(1)
            .Select(nodes =>
            {
                foreach (var node in nodes)
                {
                    var r = DeserializeRole(node);
                    if (r != null && string.Equals(r.Id, roleId, StringComparison.Ordinal))
                        return r;
                }
                return (Role?)null;
            });
    }

    public IObservable<Role> GetRoles()
    {
        // Snapshot — built-in roles always present (in case the synced query
        // hasn't surfaced their static MeshNodes yet), then any custom roles
        // from the synced collection (de-duplicated by Id).
        return ObserveAllRoleNodes()
            .Take(1)
            .SelectMany(nodes =>
            {
                var seen = ImmutableHashSet<string>.Empty;
                var result = ImmutableList<Role>.Empty;
                foreach (var br in BuiltInRoles.Values)
                {
                    seen = seen.Add(br.Id);
                    result = result.Add(br);
                }
                foreach (var node in nodes)
                {
                    var r = DeserializeRole(node);
                    if (r == null || seen.Contains(r.Id))
                        continue;
                    seen = seen.Add(r.Id);
                    result = result.Add(r);
                }
                return result;
            });
    }

    #endregion

    #region Access Assignment Management

    #endregion

    #region Partition Access Policies

    /// <inheritdoc />
    public IObservable<PartitionAccessPolicy?> GetPolicy(string targetNamespace)
    {
        var ns = targetNamespace ?? "";
        // ObserveAllPolicies returns a namespace → policy map. Read by namespace
        // directly — the in-memory map already contains every PartitionAccessPolicy
        // node in the mesh, keyed by its namespace; no extra scan needed.
        return ObserveAllPolicies()
            .Select(policies => policies.TryGetValue(ns, out var policy) ? policy : null);
    }

    #endregion
}
