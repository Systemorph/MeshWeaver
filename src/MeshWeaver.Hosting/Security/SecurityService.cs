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

    // Custom-role replay surface — every save OnNexts onto the subject; readers
    // subscribe and stay subscribed (built-in roles are always concatenated in
    // front). The replay subject IS the cache; no per-key dictionary kept.
    private readonly System.Reactive.Subjects.ReplaySubject<Role> _customRoles = new();

    // Static policies from IStaticNodeProvider (e.g., Doc, Agent, Role namespaces are read-only)
    private readonly Dictionary<string, PartitionAccessPolicy> _staticPolicies;

    // (AccessAssignments are no longer captured here — the synced workspace
    // query in ObserveAllMeshNodes() folds in static seeds AND runtime
    // CreateNode / UpdateNode / DeleteNode events.)

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

        // PartitionAccessPolicy stays a constructor-captured snapshot — there's
        // no runtime mutation surface for it yet. Last-wins on duplicate namespaces.
        _staticPolicies = allStaticNodes
            .Where(n => n.NodeType == "PartitionAccessPolicy" && n.Id == "_Policy" && n.Content is PartitionAccessPolicy)
            .GroupBy(n => n.Namespace ?? "")
            .ToDictionary(g => g.Key, g => (PartitionAccessPolicy)g.Last().Content!);
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

        // Read role IDs from the live workspace MeshNodes (when present) AND
        // from the static MeshConfiguration / IStaticNodeProvider snapshot the
        // constructor captured. The synced cross-hub query is intentionally
        // disabled (it caused infinite NodeType-hub-construction recursion);
        // SecurityService relies on static seeds + the local workspace's
        // AccessAssignments only.
        return ObserveAllMeshNodes()
            .Select(allNodes =>
            {
                var roleIds = System.Collections.Immutable.ImmutableHashSet<string>.Empty;
                var permissionCap = Permission.All;

                var scopeSet = GetScopeHierarchy(nodePath).ToHashSet();

                void Consume(MeshNode node)
                {
                    if (node.NodeType != SecurityCollections.AccessAssignmentNodeType)
                        return;
                    var ns = node.Namespace ?? "";
                    var scope = ns.EndsWith("/_Access", StringComparison.Ordinal)
                        ? ns[..^"/_Access".Length]
                        : (ns == "_Access" ? "" : null);
                    if (scope is null || !scopeSet.Contains(scope))
                        return;

                    var assignment = DeserializeAssignment(node);
                    if (assignment == null || assignment.AccessObject != userId)
                        return;
                    foreach (var ra in assignment.Roles)
                    {
                        if (string.IsNullOrEmpty(ra.Role) || ra.Denied)
                            continue;
                        roleIds = roleIds.Add(ra.Role);
                    }
                }

                foreach (var node in allNodes)
                    Consume(node);

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

    private const string AccessAssignmentQueryId = "$security-access-assignments";

    /// <summary>
    /// Live AccessAssignment <see cref="MeshNode"/> collection backed by the
    /// workspace's synced mesh-query
    /// (<see cref="SyncedQueryDataSourceExtensions.GetQuery(IWorkspace, object, string[])"/>).
    /// Get-or-create on first call; subsequent reads on this hub share the
    /// cached observable via the per-workspace registry. Static seeds AND
    /// runtime CreateNode / UpdateNode / DeleteNode of an AccessAssignment
    /// surface here automatically (Initial / Added / Updated / Removed
    /// folded into a path → MeshNode dictionary upstream).
    /// </summary>
    private IObservable<MeshNode[]> ObserveAllMeshNodes()
    {
        var workspace = _hub.GetWorkspace();
        return workspace
            .GetQuery(AccessAssignmentQueryId,
                $"nodeType:{SecurityCollections.AccessAssignmentNodeType} scope:subtree")
            .Select(arr => arr.ToArray());
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

        // Hot, replayed view of the role — emits the latest snapshot and every
        // subsequent SaveRole. No Take(1); subscribers stay subscribed.
        return _customRoles
            .Where(r => string.Equals(r.Id, roleId, StringComparison.Ordinal))
            .Select(r => (Role?)r);
    }

    public IObservable<Role> GetRoles()
        => BuiltInRoles.Values.ToObservable().Concat(_customRoles);

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
