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
internal class SecurityService : ISecurityService
{
    private readonly AccessService _accessService;
    private readonly ILogger<SecurityService> _logger;
    private readonly IMessageHub _hub;

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

    // Per-user scope-to-roles stream cache. Each entry is a hot
    // Replay(1).RefCount observable backed by an internal keep-alive
    // subscription. Multiple permission checks for the same user share a
    // single computation per synced-query emission. Idle users evict after
    // SlidingExpiration.
    private readonly IMemoryCache _userScopeRolesCache = new MemoryCache(new MemoryCacheOptions());
    private static readonly TimeSpan UserCacheTtl = TimeSpan.FromMinutes(5);

    // Shared synced-query stream: subscribe ONCE; per-user caches map off it.
    private IObservable<MeshNode[]>? _allNodesShared;
    private readonly object _allNodesLock = new();

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

        var enriched = GetUserScopeRolesStream(userId)
            .Timeout(TimeSpan.FromSeconds(2))
            .Catch<(ImmutableDictionary<string, ImmutableHashSet<string>> Granted,
                ImmutableDictionary<string, ImmutableHashSet<string>> Denied), Exception>(ex =>
            {
                _logger.LogWarning(ex,
                    "GetUserScopeRolesStream timed out for {UserId} — staying with claim + " +
                    "static seeds. AccessAssignment grants from the synced query won't apply " +
                    "until the underlying SyncedQueryRegistry produces a first emission.",
                    userId);
                return Observable.Return((Granted: ImmutableDictionary<string, ImmutableHashSet<string>>.Empty,
                    Denied: ImmutableDictionary<string, ImmutableHashSet<string>>.Empty));
            })
            .Select(snap => ComputeRoleState(snap.Granted, nodePath, userId, snap.Denied));

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
        ImmutableDictionary<string, ImmutableHashSet<string>>? scopeToDeniedRoles = null)
    {
        var roleIds = ImmutableHashSet<string>.Empty;
        var permissionCap = Permission.All;
        var isSelfScopeOwner = userId != WellKnownUsers.Anonymous
                               && userId != WellKnownUsers.Public;
        foreach (var scope in GetScopeHierarchy(nodePath))
        {
            // BreaksInheritance: the policy at this scope discards everything
            // that was inherited from ancestors — both the role grants AND any
            // partition-level cap that was being narrowed on the way down.
            // Local roles defined AT this scope (and below) still apply, so
            // we drop accumulated state BEFORE this iteration's contributions
            // land. The flag is set in addition to the per-permission switches,
            // so the same policy can still cap the local roles via the
            // GetPermissionCap call further down.
            if (_staticPolicies.TryGetValue(scope, out var policy)
                && policy.BreaksInheritance)
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
        var context = _accessService.Context ?? _accessService.CircuitContext;
        if (context?.Roles != null
            && !string.IsNullOrEmpty(context.ObjectId)
            && context.ObjectId == userId)
        {
            foreach (var roleName in context.Roles)
                roleIds = roleIds.Add(roleName);
        }

        return (roleIds, permissionCap);
    }

    /// <summary>
    /// Per-user scope-to-roles cache. First subscribe per user computes the
    /// complete scope→roles map by walking the synced AccessAssignment
    /// collection + static seeds; subsequent permission checks for the same
    /// user (within sliding TTL) get the cached snapshot immediately. The
    /// cache entry holds an internal Subscribe to keep the
    /// <c>Replay(1).RefCount</c> warm even when no external consumer is
    /// listening; eviction (sliding 5 min) disposes the keep-alive.
    /// </summary>
    private IObservable<(
        ImmutableDictionary<string, ImmutableHashSet<string>> Granted,
        ImmutableDictionary<string, ImmutableHashSet<string>> Denied)> GetUserScopeRolesStream(string userId)
    {
        return _userScopeRolesCache.GetOrCreate(userId, entry =>
        {
            entry.SlidingExpiration = UserCacheTtl;
            var stream = ObserveAllMeshNodes()
                .Select(allNodes => ComputeScopeRoles(userId, allNodes))
                .DistinctUntilChanged()
                .Replay(1)
                .RefCount();
            // Keep-alive subscription so RefCount doesn't tear down when
            // no external subscribers are connected; disposed on cache eviction.
            var keepAlive = stream.Subscribe(_ => { }, _ => { });
            entry.RegisterPostEvictionCallback((_, _, _, _) => keepAlive.Dispose());
            return stream;
        })!;
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
    /// → IMeshQueryProvider → InMemoryMeshQuery's QueryAsync → RlsNodeValidator
    /// → SecurityService → here) is now broken at the validator: SyncedQueryMeshNodes
    /// runs the upstream <see cref="IMeshQueryProvider.ObserveQuery"/> call
    /// with <see cref="WellKnownUsers.System"/>, and
    /// <c>RlsNodeValidator.Validate</c> short-circuits to <c>Valid</c> for
    /// that identity. No re-entrant call back into this method.</para>
    /// </summary>
    private IObservable<MeshNode[]> ObserveAllMeshNodes()
    {
        if (_allNodesShared != null)
            return _allNodesShared;
        lock (_allNodesLock)
        {
            if (_allNodesShared != null)
                return _allNodesShared;
            var workspace = _hub.GetWorkspace();
            // No StartWith here — an earlier attempt to emit Array.Empty up front
            // turned out to be its own bug. AccessControlPipeline takes the FIRST
            // emission (.Take(1)); a synthetic empty fired before the real synced
            // data resolved zeroed the user's roles → DENY, even when their
            // legitimate Admin AccessAssignment would have resolved a few hundred
            // ms later. The right shape is to let the chain take its natural time
            // up to a generous bound and have the AccessControlPipeline carry the
            // safety-net timeout (currently 10 s, fail-closed on expiry).
            _allNodesShared = workspace
                .GetQuery(AccessAssignmentQueryId,
                    $"nodeType:{SecurityCollections.AccessAssignmentNodeType} scope:subtree")
                .Select(arr => arr.ToArray())
                .Replay(1)
                .RefCount();
            return _allNodesShared;
        }
    }

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
        var path = string.IsNullOrEmpty(ns) ? "_Policy" : $"{ns}/_Policy";

        // Read from the assembled MeshNode collection — the workspace already
        // aggregates across data sources. No path-specific round-trip.
        return ObserveAllMeshNodes()
            .Select(all =>
            {
                var node = all.FirstOrDefault(n =>
                    n.NodeType == SecurityCollections.PartitionAccessPolicyNodeType
                    && string.Equals(n.Path, path, StringComparison.Ordinal));
                return node is null ? null : DeserializePolicy(node);
            });
    }

    #endregion
}
