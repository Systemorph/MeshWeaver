using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Static algorithm — the row-level-security permission evaluator. Pure
/// functions over <see cref="IMessageHub"/> + the process-wide
/// <see cref="IMeshNodeStreamCache"/>; no per-hub service instance, no
/// IMemoryCache layer. Per-scope state lives entirely in the cache via
/// <c>cache.GetQuery($"$security-access:{scope}", ...)</c> /
/// <c>cache.GetQuery($"$security-policy:{scope}", ...)</c> — shared across
/// every hub in the process.
///
/// <para>Application code never calls these directly — go through
/// <see cref="HubPermissionExtensions"/> (<c>hub.CheckPermission</c> /
/// <c>hub.GetEffectivePermissions</c>).</para>
/// </summary>
internal static class PermissionEvaluator
{
    // Built-in role definitions — fast in-memory path for the common case
    // (most prod tenants don't define custom Role MeshNodes).
    private static readonly Dictionary<string, Role> BuiltInRoles = new()
    {
        { "Admin", Role.Admin },
        { "Editor", Role.Editor },
        { "Viewer", Role.Viewer },
        { "Commenter", Role.Commenter },
        { "PlatformAdmin", Role.PlatformAdmin }
    };

    private static readonly IReadOnlyDictionary<string, Permission> BuiltInRolePerms =
        BuiltInRoles.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Permissions, StringComparer.Ordinal);

    // Sanctioned dedicated identity for the IMeshNodeStreamCache hydrator;
    // granted Read only (see MeshNodeCacheIdentity in Hosting). Inlined as a
    // string literal to keep this class in Mesh.Contract — moving the
    // identity here would require pulling internal Hosting types up.
    private const string MeshNodeCacheIdentityAddress = "cache/mesh-node-cache";

    /// <summary>
    /// The platform scope. A "global admin" is, canonically, an admin on the
    /// <b>Admin partition</b> — <see cref="Permission.All"/> at this scope, granted
    /// by an <c>AccessAssignment</c> in the <c>Admin/_Access</c> namespace. The
    /// Admin partition is a standard partition that holds platform-level data; being
    /// admin on it makes you a platform superuser (see the global-admin short-circuit
    /// in <see cref="GetEffectivePermissions(IMessageHub,string,string)"/> and
    /// <c>hub.IsGlobalAdmin()</c>). Documented in Doc/Architecture/AccessControl.md.
    /// </summary>
    internal const string AdminScope = "Admin";

    private const string RoleNodeType = "Role";
    private const string RoleQueryId = "$security-roles";

    #region Public surface

    public static IObservable<bool> HasPermission(IMessageHub hub, string nodePath, Permission permission)
    {
        if (permission == Permission.None)
            return Observable.Return(true);
        var userId = ResolveUserId(hub);
        return HasPermission(hub, nodePath, userId, permission);
    }

    public static IObservable<bool> HasPermission(IMessageHub hub, string nodePath, string userId, Permission permission)
    {
        if (permission == Permission.None)
            return Observable.Return(true);
        return GetEffectivePermissions(hub, nodePath, userId)
            .Select(p => p.HasFlag(permission));
    }

    public static IObservable<Permission> GetEffectivePermissions(IMessageHub hub, string nodePath)
    {
        var userId = ResolveUserId(hub);
        return GetEffectivePermissions(hub, nodePath, userId);
    }

    public static IObservable<Permission> GetEffectivePermissions(IMessageHub hub, string nodePath, string userId)
    {
        if (string.IsNullOrEmpty(userId))
            userId = WellKnownUsers.Anonymous;

        // System identity has full access — literally every permission, including the
        // privileged grants (Sync, Compile) deliberately excluded from Permission.All. An
        // explicit CheckPermission(System, Compile/Sync) must pass; the infra recompile that
        // fills the assembly cache runs under this identity.
        if (userId == WellKnownUsers.System)
            return Observable.Return(Permission.All | Permission.Sync | Permission.Compile);

        // MeshNodeCache's hydrator identity — granted Read only.
        if (userId == MeshNodeCacheIdentityAddress)
            return Observable.Return(Permission.Read);

        var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Mesh.Security.PermissionEvaluator");
        var staticNodes = CollectStaticAccessAssignments(hub);
        var staticPolicies = CollectStaticPolicies(hub);

        // 🚨 Capture AccessContext on the CALLER'S thread before any Rx
        // scheduler hop. AsyncLocal does NOT flow through SubscribeOn/
        // ObserveOn — when the .Select lambdas below land on TaskPool
        // (because cache.GetQuery uses SubscribeOn(TaskPoolScheduler)),
        // accessService.Context is null or contaminated. CircuitContext is
        // mesh-global so it survives, but Bearer-token Roles claims live in
        // AccessContext.Roles and we need that snapshot here.
        var capturedContext = accessService?.Context;
        var capturedCircuitContext = accessService?.CircuitContext;

        // 🛡️ Hub credential (ImpersonateAsHub): ObjectId is the hub's OWN mesh address, never a
        // user/group identity — no AccessAssignment ever exists for a hub address. A hub
        // initializes + syncs its own EntityStore under this credential, and a sub-hub subscribes
        // to its parent/owner under it (JsonSynchronizationStream.CreateExternalClient with
        // impersonateAsHub: true). Grant Read on the hub's OWN path and its ANCESTOR scopes (the
        // sync direction) — never siblings or descendants. Returning here also keeps hub self-sync
        // off the cold permission-query path entirely. See AccessControl.md → "Hub credentials".
        var hubCredential = capturedContext ?? capturedCircuitContext;
        if (hubCredential?.IsHub == true
            && string.Equals(userId, hubCredential.ObjectId, StringComparison.Ordinal)
            && IsHubReadableScope(userId, nodePath))
            return Observable.Return(Permission.Read);

        // Claim-first composition: static + claim-based roles available
        // synchronously. Emit immediately; then enrich asynchronously with
        // the synced AccessAssignment query (so long-lived subscribers see
        // updates as runtime grants land).
        var staticOnlyScopeRoles = ComputeStaticOnlyScopeRoles(staticNodes, userId, hub.JsonSerializerOptions);
        var staticOnlyDeniedScopeRoles = ComputeStaticOnlyDeniedScopeRoles(staticNodes, userId, hub.JsonSerializerOptions);
        var fast = ComputeRoleState(staticOnlyScopeRoles, nodePath, userId, capturedContext, capturedCircuitContext, staticPolicies, staticOnlyDeniedScopeRoles);

        var enriched = ObserveEffectiveAssignments(hub, cache, nodePath, staticNodes)
            .CombineLatest(
                ObserveScopePolicies(hub, cache, nodePath, staticPolicies),
                (nodes, policies) =>
                {
                    var (granted, denied) = ComputeScopeRoles(userId, nodes, staticNodes, hub.JsonSerializerOptions);
                    return (Granted: granted, Denied: denied, RuntimePolicies: policies);
                })
            .Select(snap => ComputeRoleState(snap.Granted, nodePath, userId, capturedContext, capturedCircuitContext, staticPolicies, snap.Denied, snap.RuntimePolicies));

        // Emit the synchronous static snapshot whenever it carries ANY signal —
        // roles OR a static public-read grant. The public grant is computed from
        // static policies (collected synchronously above), so a PublicRead catalog
        // (e.g. the built-in Agent namespace) yields Read on the FIRST emission with
        // no wait for the synced AccessAssignment/Policy queries. Skipping the seed
        // on RoleIds-only left role-less readers of a public catalog blocked on the
        // synced cold-start path — the "No suitable agent" race during execution.
        var seed = (fast.RoleIds.Count > 0 || fast.PublicGrant != Permission.None)
            ? Observable.Return(fast)
            : Observable.Empty<(ImmutableHashSet<string>, Permission, Permission)>();

        return seed.Concat(enriched)
            .SelectMany(state =>
            {
                var (roleIds, permissionCap, publicGrant) = state;

                // Fast path: every role is built-in → resolve synchronously.
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
                            .Select(id => GetRole(hub, id))
                            .Merge()
                            .Where(r => r is not null)
                            .Aggregate(Permission.None, (acc, r) => acc | r!.Permissions),
                        (builtIn, custom) => builtIn | custom);

                IObservable<Permission> withPublic = (userId != WellKnownUsers.Anonymous && userId != WellKnownUsers.Public)
                    ? rolePerms.Zip(GetEffectivePermissions(hub, nodePath, WellKnownUsers.Public),
                        (own, pub) => own | pub)
                    : rolePerms;

                return withPublic.Select(p =>
                {
                    p &= permissionCap;
                    p |= publicGrant;   // public-read override — precedence over (roles ∩ cap)
                    // Use the snapshot captured on caller's thread, NOT
                    // accessService.Context (AsyncLocal doesn't flow through
                    // the Rx schedulers cache.GetQuery uses).
                    var currentContext = capturedContext ?? capturedCircuitContext;
                    if (currentContext?.IsApiToken == true && !p.HasFlag(Permission.Api))
                        p = Permission.None;
                    logger?.LogTrace("User {UserId} has permissions {Permissions} on node {NodePath} (cap: {Cap})",
                        userId, p, nodePath, permissionCap);
                    return p;
                });
            })
            .DistinctUntilChanged();
    }

    public static IObservable<Role?> GetRole(IMessageHub hub, string roleId)
    {
        if (string.IsNullOrEmpty(roleId))
            return Observable.Return<Role?>(null);
        if (BuiltInRoles.TryGetValue(roleId, out var builtIn))
            return Observable.Return<Role?>(builtIn);

        return ObserveAllRoleNodes(hub)
            .Take(1)
            .Select(nodes =>
            {
                foreach (var node in nodes)
                {
                    var r = DeserializeRole(node, hub.JsonSerializerOptions);
                    if (r != null && string.Equals(r.Id, roleId, StringComparison.Ordinal))
                        return r;
                }
                return (Role?)null;
            });
    }

    public static IObservable<Role> GetRoles(IMessageHub hub)
    {
        return ObserveAllRoleNodes(hub)
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
                    var r = DeserializeRole(node, hub.JsonSerializerOptions);
                    if (r == null || seen.Contains(r.Id))
                        continue;
                    seen = seen.Add(r.Id);
                    result = result.Add(r);
                }
                return result;
            });
    }

    public static IObservable<PartitionAccessPolicy?> GetPolicy(IMessageHub hub, string targetNamespace)
    {
        var ns = targetNamespace ?? "";
        var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var staticPolicies = CollectStaticPolicies(hub);
        return ObserveScopePolicies(hub, cache, ns, staticPolicies)
            .Select(policies => policies.TryGetValue(ns, out var policy) ? policy : null);
    }

    #endregion

    #region Static node collection

    private static IReadOnlyList<MeshNode> CollectStaticAccessAssignments(IMessageHub hub)
    {
        var providers = hub.ServiceProvider.GetServices<IStaticNodeProvider>();
        var result = new List<MeshNode>();
        foreach (var p in providers)
        {
            foreach (var n in p.GetStaticNodes())
            {
                if (n.NodeType == SecurityCollections.AccessAssignmentNodeType && n.Content != null)
                    result.Add(n);
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, PartitionAccessPolicy> CollectStaticPolicies(IMessageHub hub)
    {
        var providers = hub.ServiceProvider.GetServices<IStaticNodeProvider>();
        var result = new Dictionary<string, PartitionAccessPolicy>(StringComparer.Ordinal);
        foreach (var p in providers)
        {
            foreach (var n in p.GetStaticNodes())
            {
                if (n.NodeType == SecurityCollections.PartitionAccessPolicyNodeType
                    && n.Id == "_Policy"
                    && n.Content is PartitionAccessPolicy policy)
                {
                    result[n.Namespace ?? ""] = policy;
                }
            }
        }
        return result;
    }

    #endregion

    #region Static-only scope-role walks (synchronous claim path)

    private static ImmutableDictionary<string, ImmutableHashSet<string>> ComputeStaticOnlyScopeRoles(
        IReadOnlyList<MeshNode> staticNodes, string userId, JsonSerializerOptions options)
    {
        var result = ImmutableDictionary<string, ImmutableHashSet<string>>.Empty;
        foreach (var node in staticNodes)
        {
            if (node.NodeType != SecurityCollections.AccessAssignmentNodeType)
                continue;
            var ns = node.Namespace ?? "";
            var scope = ns.EndsWith("/_Access", StringComparison.Ordinal)
                ? ns[..^"/_Access".Length]
                : (ns == "_Access" ? "" : null);
            if (scope is null)
                continue;
            var assignment = DeserializeAssignment(node, options);
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
        return result;
    }

    private static ImmutableDictionary<string, ImmutableHashSet<string>> ComputeStaticOnlyDeniedScopeRoles(
        IReadOnlyList<MeshNode> staticNodes, string userId, JsonSerializerOptions options)
    {
        var result = ImmutableDictionary<string, ImmutableHashSet<string>>.Empty;
        foreach (var node in staticNodes)
        {
            if (node.NodeType != SecurityCollections.AccessAssignmentNodeType)
                continue;
            var ns = node.Namespace ?? "";
            var scope = ns.EndsWith("/_Access", StringComparison.Ordinal)
                ? ns[..^"/_Access".Length]
                : (ns == "_Access" ? "" : null);
            if (scope is null)
                continue;
            var assignment = DeserializeAssignment(node, options);
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
        return result;
    }

    #endregion

    #region Scope hierarchy / role-state composition

    private static (ImmutableHashSet<string> RoleIds, Permission PermissionCap, Permission PublicGrant) ComputeRoleState(
        ImmutableDictionary<string, ImmutableHashSet<string>> scopeToRoles,
        string nodePath,
        string userId,
        // Captured snapshots from the caller's thread (not read via
        // AsyncLocal here — this method may run on a Rx scheduler thread).
        AccessContext? capturedContext,
        AccessContext? capturedCircuitContext,
        IReadOnlyDictionary<string, PartitionAccessPolicy> staticPolicies,
        ImmutableDictionary<string, ImmutableHashSet<string>>? scopeToDeniedRoles = null,
        ImmutableDictionary<string, PartitionAccessPolicy>? runtimePolicies = null)
    {
        var roleIds = ImmutableHashSet<string>.Empty;
        // ALL BITS SET = "no cap". `p &= permissionCap` (line ~179) must never strip a permission
        // a role legitimately grants. Permission.All excludes the privileged bits (Sync, Compile),
        // so using it as the default cap silently masked Compile out of every Editor/Admin's
        // effective set — the Compile gate then refused the very users meant to hold it.
        var permissionCap = (Permission)~0;
        var publicGrant = Permission.None;
        var isSelfScopeOwner = userId != WellKnownUsers.Anonymous
                               && userId != WellKnownUsers.Public;
        foreach (var scope in GetScopeHierarchy(nodePath))
        {
            PartitionAccessPolicy? policy = null;
            if (runtimePolicies is not null && runtimePolicies.TryGetValue(scope, out var rp))
                policy = rp;
            else if (staticPolicies.TryGetValue(scope, out var sp))
                policy = sp;

            if (policy is not null && policy.BreaksInheritance)
            {
                roleIds = ImmutableHashSet<string>.Empty;
                permissionCap = (Permission)~0;   // reset to "no cap" (all bits) — see above
            }

            if (scopeToRoles.TryGetValue(scope, out var roles))
                roleIds = roleIds.Union(roles);
            if (policy is not null)
            {
                permissionCap &= policy.GetPermissionCap();
                // Public-read override: a policy with PublicRead grants Read to every
                // user at this scope and below. Accumulated here, ORed in AFTER the
                // per-user (roles ∩ cap) below — so it has precedence and needs no role.
                if (policy.PublicRead)
                    publicGrant |= Permission.Read;
            }

            if (scopeToDeniedRoles is not null
                && scopeToDeniedRoles.TryGetValue(scope, out var deniedRoles))
                roleIds = roleIds.Except(deniedRoles);

            if (isSelfScopeOwner
                && string.Equals(scope, userId, StringComparison.OrdinalIgnoreCase))
                roleIds = roleIds.Add(Role.Admin.Id);
        }

        AddClaimRoles(capturedContext);
        AddClaimRoles(capturedCircuitContext);

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

        return (roleIds, permissionCap, publicGrant);
    }

    private static List<string> GetScopeHierarchy(string nodePath)
    {
        var scopes = new List<string> { "" };
        if (!string.IsNullOrEmpty(nodePath))
        {
            var segments = nodePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i <= segments.Length; i++)
                scopes.Add(string.Join("/", segments.Take(i)));
        }
        return scopes;
    }

    private static string GetParentScope(string scope)
    {
        if (string.IsNullOrEmpty(scope)) return string.Empty;
        var idx = scope.LastIndexOf('/');
        return idx < 0 ? string.Empty : scope[..idx];
    }

    /// <summary>
    /// True when <paramref name="hubAddress"/> may Read <paramref name="scope"/> as a hub
    /// credential: the scope lies on the hub's OWN VERTICAL CHAIN — the hub's path itself, an
    /// ANCESTOR of it (EntityStore self-sync + a sub-hub reading its parent/owner), or a
    /// DESCENDANT of it (the hub reading its own subtree: child cells, satellites). SIBLINGS and
    /// the empty (mesh) root are NOT readable — a hub never reaches sideways out of its own chain.
    /// </summary>
    private static bool IsHubReadableScope(string hubAddress, string scope)
        => !string.IsNullOrEmpty(scope)
            && (string.Equals(hubAddress, scope, StringComparison.Ordinal)
                || hubAddress.StartsWith(scope + "/", StringComparison.Ordinal)   // scope is an ancestor of the hub
                || scope.StartsWith(hubAddress + "/", StringComparison.Ordinal));  // scope is a descendant of the hub

    private static (ImmutableDictionary<string, ImmutableHashSet<string>> Granted,
                    ImmutableDictionary<string, ImmutableHashSet<string>> Denied) ComputeScopeRoles(
        string userId,
        IEnumerable<MeshNode> allNodes,
        IReadOnlyList<MeshNode> staticAssignments,
        JsonSerializerOptions options)
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

            var assignment = DeserializeAssignment(node, options);
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
        foreach (var node in staticAssignments)
            Consume(node);

        return (granted, denied);
    }

    #endregion

    #region Per-scope observable chains (AccessAssignment + Policy)

    private static IObservable<IEnumerable<MeshNode>> ObserveScopeAssignments(
        IMessageHub hub, IMeshNodeStreamCache cache, string scope, IReadOnlyList<MeshNode> staticNodes)
    {
        var key = scope ?? string.Empty;
        var nsQuery = string.IsNullOrEmpty(key) ? "_Access" : $"{key}/_Access";

        // The Admin partition is EXCLUDED from cross-schema global search
        // (PostgreSqlSchemaInitializer.searchable_schemas), so a namespace-only access query
        // never reaches admin.access — platform-admin grants would silently never load and a
        // platform admin is unrecognized on Postgres. For Admin-rooted scopes, route by PATH:
        // `path:{scope}/_Access` resolves to the admin schema via its first segment
        // (PostgreSqlPartitionedMeshQuery.FirstSegment) and to the access table via nodeType.
        // Every other scope keeps the namespace query — those schemas ARE in the cross-schema
        // search, and path/namespace select the same flat grant set under {scope}/_Access.
        var isAdminScope = key == AdminScope
            || key.StartsWith(AdminScope + "/", StringComparison.Ordinal);
        var selfFilter = isAdminScope
            ? $"path:{nsQuery} scope:children nodeType:{SecurityCollections.AccessAssignmentNodeType}"
            : $"namespace:{nsQuery} nodeType:{SecurityCollections.AccessAssignmentNodeType}";

        // Self: narrow per-scope query against the singleton cache. Each
        // scope's stream is cached PROCESS-WIDE under the key
        // "$security-access:{scope}" — every hub in the process shares one
        // upstream subscription per scope.
        var self = cache.GetQuery($"$security-access:{key}", hub.JsonSerializerOptions, selfFilter);

        // Parent: recursive reference to parent-scope cached observable.
        // Root scope folds in statics instead.
        IObservable<IEnumerable<MeshNode>> parentOrBase = string.IsNullOrEmpty(key)
            ? Observable.Return<IEnumerable<MeshNode>>(staticNodes.ToArray())
            : ObserveScopeAssignments(hub, cache, GetParentScope(key), staticNodes);

        return Observable.CombineLatest(self, parentOrBase, UnionByPath)
            .DistinctUntilChanged(MeshNodeListPathEquality.Instance);
    }

    private static IObservable<IEnumerable<MeshNode>> ObserveEffectiveAssignments(
        IMessageHub hub, IMeshNodeStreamCache cache, string nodePath, IReadOnlyList<MeshNode> staticNodes)
        => ObserveScopeAssignments(hub, cache, nodePath ?? string.Empty, staticNodes);

    private static IObservable<ImmutableDictionary<string, PartitionAccessPolicy>> ObserveScopePolicies(
        IMessageHub hub, IMeshNodeStreamCache cache, string scope,
        IReadOnlyDictionary<string, PartitionAccessPolicy> staticPolicies)
    {
        var key = scope ?? string.Empty;
        var nsFilter = string.IsNullOrEmpty(key)
            ? "namespace: id:_Policy"
            : $"namespace:{key} id:_Policy";

        var self = cache.GetQuery(
            $"$security-policy:{key}",
            hub.JsonSerializerOptions,
            $"{nsFilter} nodeType:{SecurityCollections.PartitionAccessPolicyNodeType}");

        IObservable<ImmutableDictionary<string, PartitionAccessPolicy>> parentOrBase;
        if (string.IsNullOrEmpty(key))
        {
            var staticMap = staticPolicies.Aggregate(
                ImmutableDictionary<string, PartitionAccessPolicy>.Empty,
                (acc, kvp) => acc.SetItem(kvp.Key, kvp.Value));
            parentOrBase = Observable.Return(staticMap);
        }
        else
        {
            parentOrBase = ObserveScopePolicies(hub, cache, GetParentScope(key), staticPolicies);
        }

        var options = hub.JsonSerializerOptions;
        return Observable.CombineLatest(self, parentOrBase,
            (selfNodes, parentMap) =>
            {
                var dict = parentMap;
                foreach (var node in selfNodes)
                {
                    if (node.Id != "_Policy") continue;
                    var policy = node.Content as PartitionAccessPolicy
                                 ?? DeserializePolicy(node, options);
                    if (policy is null) continue;
                    dict = dict.SetItem(node.Namespace ?? string.Empty, policy);
                }
                return dict;
            })
            .DistinctUntilChanged();
    }

    private static IObservable<MeshNode[]> ObserveAllRoleNodes(IMessageHub hub)
    {
        var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        return cache.GetQuery(RoleQueryId, hub.JsonSerializerOptions, $"nodeType:{RoleNodeType} scope:subtree")
            .Select(arr => arr.ToArray());
    }

    #endregion

    #region Helpers

    private static string ResolveUserId(IMessageHub hub)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var context = accessService?.Context ?? accessService?.CircuitContext;
        var userId = context?.ObjectId;
        if (string.IsNullOrEmpty(userId) || context?.IsVirtual == true)
            userId = WellKnownUsers.Anonymous;
        return userId;
    }

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

    private static AccessAssignment? DeserializeAssignment(MeshNode node, JsonSerializerOptions options)
    {
        if (node.Content is AccessAssignment aa)
            return aa;
        if (node.Content is JsonElement je)
        {
            try
            {
                return JsonSerializer.Deserialize<AccessAssignment>(je.GetRawText(), options);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    private static PartitionAccessPolicy? DeserializePolicy(MeshNode node, JsonSerializerOptions options)
    {
        if (node.Content is PartitionAccessPolicy policy)
            return policy;
        if (node.Content is JsonElement je)
        {
            try
            {
                return JsonSerializer.Deserialize<PartitionAccessPolicy>(je.GetRawText(), options);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    private static Role? DeserializeRole(MeshNode node, JsonSerializerOptions options)
    {
        if (node.Content is Role r)
            return r;
        if (node.Content is JsonElement je)
        {
            try
            {
                return JsonSerializer.Deserialize<Role>(je.GetRawText(), options);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    #endregion
}
