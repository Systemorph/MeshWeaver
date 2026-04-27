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

        // Also include AccessAssignment nodes from MeshConfiguration (e.g., PublicAdminAccess)
        if (meshConfiguration != null)
        {
            allStaticNodes.AddRange(
                meshConfiguration.Nodes.Values
                    .Where(n => n.NodeType == "AccessAssignment"));
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

        // Build the user's complete scope→roles map ONCE from the synced
        // AccessAssignment query (cached + refcounted, refreshed on each
        // assignment change). All permission checks for this user share the
        // same subscription/computation per synced-query emission.
        return GetUserScopeRolesStream(userId)
            .Select(scopeToRoles =>
            {
                var roleIds = ImmutableHashSet<string>.Empty;
                var permissionCap = Permission.All;
                foreach (var scope in GetScopeHierarchy(nodePath))
                {
                    if (scopeToRoles.TryGetValue(scope, out var roles))
                        roleIds = roleIds.Union(roles);
                    if (_staticPolicies.TryGetValue(scope, out var policy))
                        permissionCap &= policy.GetPermissionCap();
                }

                // Add claim-based roles from AccessContext.
                var context = _accessService.Context ?? _accessService.CircuitContext;
                if (context?.Roles != null
                    && !string.IsNullOrEmpty(context.ObjectId)
                    && context.ObjectId == userId)
                {
                    foreach (var roleName in context.Roles)
                        roleIds = roleIds.Add(roleName);
                }

                return (roleIds, permissionCap);
            })
            .SelectMany(state =>
            {
                var (roleIds, permissionCap) = state;
                var rolePerms = roleIds.IsEmpty
                    ? Observable.Return(Permission.None)
                    : roleIds
                        .Select(GetRole)
                        .Merge()
                        .Where(r => r is not null)
                        .Aggregate(Permission.None, (acc, r) => acc | r!.Permissions);

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
            });
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
    private IObservable<ImmutableDictionary<string, ImmutableHashSet<string>>> GetUserScopeRolesStream(string userId)
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

    private ImmutableDictionary<string, ImmutableHashSet<string>> ComputeScopeRoles(
        string userId, IEnumerable<MeshNode> allNodes)
    {
        var result = ImmutableDictionary<string, ImmutableHashSet<string>>.Empty;

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
                if (string.IsNullOrEmpty(ra.Role) || ra.Denied)
                    continue;
                var existing = result.TryGetValue(scope, out var roles)
                    ? roles
                    : ImmutableHashSet<string>.Empty;
                result = result.SetItem(scope, existing.Add(ra.Role));
            }
        }

        foreach (var node in allNodes)
            Consume(node);
        foreach (var (_, list) in _staticAccessAssignments)
            foreach (var node in list)
                Consume(node);

        return result;
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
