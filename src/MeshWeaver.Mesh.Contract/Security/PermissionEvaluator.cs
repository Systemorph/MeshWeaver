using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// by an <c>AccessAssignment</c> in the <c>Admin/_Access</c> namespace, reported by
    /// <c>hub.IsGlobalAdmin()</c>. The Admin partition is a standard partition holding
    /// platform-level data (invitations, inbox, models, global settings).
    ///
    /// <para>🚨 A global admin is NOT a data superuser, and there is NO global-admin
    /// short-circuit in <see cref="GetEffectivePermissions(IMessageHub,string,string)"/>.
    /// Being admin here grants NOTHING anywhere else — in particular <b>no Read on any
    /// other node</b>. That falls straight out of how scope works: a grant's scope is its
    /// own <c>{scope}/_Access</c> namespace (see <c>ComputeScopeRoles</c>) and only the
    /// ancestors of the target path are consulted (<c>GetScopeHierarchy</c>), so an
    /// <c>Admin/_Access</c> grant is simply never in a space's or user's scope walk.
    /// Purchased/gated content is therefore gated for admins too — entitlement is a record,
    /// never a side effect of being an admin. The ONLY blanket short-circuits below are for
    /// <see cref="WellKnownUsers.System"/> and a hub's own sync credential.</para>
    ///
    /// <para>Pinned by <c>AdminPartitionAdminTests.PlatformAdmin_HasNoStandingAccessToSpacesOrUsers</c>
    /// and <c>…_GrantsNoReadOnAnyOtherNode</c>. Documented in Doc/Architecture/AccessControl.md.
    /// Emergency cross-partition access is an explicit elevation (break-glass), never standing
    /// permission — do not "restore" a superuser branch here.</para>
    /// </summary>
    internal const string AdminScope = "Admin";

    private const string RoleNodeType = "Role";
    private const string RoleQueryId = "$security-roles";

    // Group access is resolved GLOBALLY — a group defined in one partition can be granted in
    // another (cross-partition licensing), so we read EVERY GroupMembership node (as System, like
    // the other $security-* queries) and expand the viewer's transitive group set in-memory. This
    // mirrors the Postgres rebuild, which reads memberships from the global auth mirror.
    private const string GroupMembershipNodeType = "GroupMembership";
    private const string MembershipQueryId = "$security-memberships";

    // Per-gated-type cached query key prefix — one shared upstream subscription per gated node
    // type, process-wide, like every other $security-* query.
    private const string GatedQueryPrefix = "$security-gated:";

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

        // Type-declared subtree gates (issue #701). EMPTY on every mesh that declares none, and
        // every branch below short-circuits on that — a deployment without gates runs the exact
        // fold it ran before, subscribes no extra query and pays nothing.
        var gates = CollectGates(hub);
        var staticGatedNodes = CollectStaticGatedNodes(hub, gates);

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
        // A gate declared over a STATIC node resolves synchronously — the declared public surface
        // is readable on the first emission, with no wait on the synced queries (same reasoning as
        // the static PublicRead policy seeded below).
        fast = (fast.RoleIds, fast.PermissionCap,
            fast.PublicGrant | GateGrant(gates, staticGatedNodes, nodePath));

        var enriched = Observable.CombineLatest(
                ObserveEffectiveAssignments(hub, cache, nodePath, staticNodes),
                ObserveScopePolicies(hub, cache, nodePath, staticPolicies),
                ObserveAllMembershipNodes(hub),
                ObserveGatedNodes(hub, cache, gates, staticGatedNodes),
                (nodes, policies, memberships, gatedNodes) =>
                {
                    // Match grants to the viewer OR any group they belong to (transitively). A group
                    // grant's subject is the group path, and memberships live UNDER the group node —
                    // off the target's scope walk, and possibly in another partition — so the group
                    // set is resolved globally here, then folded into the subjects ComputeScopeRoles
                    // matches on. Consistent with the Postgres rebuild's global group expansion.
                    var subjects = ResolveUserGroups(userId, memberships, hub.JsonSerializerOptions).Add(userId);
                    var (granted, denied) = ComputeScopeRoles(subjects, nodes, staticNodes, hub.JsonSerializerOptions);
                    return (Granted: granted, Denied: denied, RuntimePolicies: policies, GatedNodes: gatedNodes);
                })
            .Select(snap =>
            {
                var state = ComputeRoleState(snap.Granted, nodePath, userId, capturedContext, capturedCircuitContext, staticPolicies, snap.Denied, snap.RuntimePolicies);
                // The gate contributes to the PUBLIC grant — ORed in after (roles ∩ cap), exactly
                // like PartitionAccessPolicy.PublicRead — so a declared public surface needs no
                // role and no AccessAssignment row of any kind. It only ever ADDS Read.
                return (state.RoleIds, state.PermissionCap,
                    state.PublicGrant | GateGrant(gates, snap.GatedNodes, nodePath));
            });

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
                    // API-token capability: the token may use the API surface when its NODE
                    // permissions carry Api (a real Viewer/Editor grant) OR its claim roles do —
                    // this is the ONE place claim roles act, and it is a CAPABILITY gate, never a
                    // data grant (claims are excluded from roleIds above; see ComputeRoleState).
                    // Without the claims half, removing claims from node permissions would zero
                    // every MCP token on PublicRead-policy content (p = Read only, no Api bit).
                    if (currentContext?.IsApiToken == true && !p.HasFlag(Permission.Api)
                        && !ClaimsCarryApi(currentContext))
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

    /// <summary>
    /// The effective <see cref="PartitionAccessPolicy.RedirectOnDenied"/> for <paramref name="targetNamespace"/>:
    /// the nearest scope — self, then each ancestor up to root — that sets one, normalized (no leading '/'),
    /// or <c>null</c> if none. Reuses the exact scope-policy chain the permission evaluation reads, so a
    /// policy set once at a partition root applies to every node beneath it.
    /// </summary>
    public static IObservable<string?> GetRedirectOnDenied(IMessageHub hub, string targetNamespace)
    {
        var ns = targetNamespace ?? "";
        var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var staticPolicies = CollectStaticPolicies(hub);
        var fromPolicy = ObserveScopePolicies(hub, cache, ns, staticPolicies)
            .Select(policies =>
            {
                // Nearest scope (self first) with a RedirectOnDenied wins; walk to root.
                for (var s = ns; ; s = GetParentScope(s))
                {
                    if (policies.TryGetValue(s, out var p) && !string.IsNullOrWhiteSpace(p.RedirectOnDenied))
                        return p.RedirectOnDenied!.TrimStart('/');
                    if (string.IsNullOrEmpty(s)) break;
                }
                return (string?)null;
            });

        // Type-declared fallback (issue #701): a NodeTypeGate supplies the redirect its instances
        // would otherwise each have to materialise into a `_Policy` node. An actual `_Policy` STILL
        // WINS — a deployment mid-migration keeps every hand-tuned policy it already carries, and
        // this only fills the gap where none exists.
        var gates = CollectGates(hub);
        if (gates.Count == 0)
            return fromPolicy.DistinctUntilChanged();

        var staticGatedNodes = CollectStaticGatedNodes(hub, gates);
        return Observable.CombineLatest(
                fromPolicy,
                ObserveGatedNodes(hub, cache, gates, staticGatedNodes),
                (policyRedirect, gatedNodes) => policyRedirect
                    ?? NodeTypeGateEvaluator.ResolveRedirect(gates, gatedNodes, ns))
            .DistinctUntilChanged();
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

    /// <summary>
    /// The mesh's type-declared subtree gates (<see cref="NodeTypeGate"/>), or an empty list when
    /// none is configured — which is the default and the fast path everywhere it is consulted.
    /// </summary>
    private static IReadOnlyList<NodeTypeGate> CollectGates(IMessageHub hub)
        => hub.ServiceProvider.GetService<MeshConfiguration>()?.NodeTypeGates ?? [];

    /// <summary>
    /// The STATIC nodes whose type carries a gate, as <c>path → nodeType</c>. Static providers are
    /// read synchronously, so a gate anchored on a statically declared node applies on the very
    /// first emission with no synced-query cold start.
    /// </summary>
    private static ImmutableDictionary<string, string> CollectStaticGatedNodes(
        IMessageHub hub, IReadOnlyList<NodeTypeGate> gates)
    {
        if (gates.Count == 0)
            return ImmutableDictionary<string, string>.Empty;

        var gatedTypes = new HashSet<string>(
            gates.Select(g => g.NodeType), StringComparer.OrdinalIgnoreCase);
        var result = ImmutableDictionary<string, string>.Empty;
        foreach (var provider in hub.ServiceProvider.GetServices<IStaticNodeProvider>())
        {
            foreach (var node in provider.GetStaticNodes())
            {
                if (string.IsNullOrEmpty(node.NodeType) || string.IsNullOrEmpty(node.Path))
                    continue;
                if (gatedTypes.Contains(node.NodeType))
                    result = result.SetItem(node.Path, node.NodeType);
            }
        }
        return result;
    }

    /// <summary>
    /// <see cref="Permission.Read"/> when <paramref name="nodePath"/> sits on a declared public
    /// surface of its nearest gated ancestor-or-self; otherwise <see cref="Permission.None"/>.
    /// A gate NEVER subtracts — "everything else is closed" is the framework's pre-existing
    /// deny-by-default, not something the gate asserts.
    /// </summary>
    private static Permission GateGrant(
        IReadOnlyList<NodeTypeGate> gates,
        IReadOnlyDictionary<string, string> gatedNodes,
        string nodePath)
        => gates.Count > 0
           && NodeTypeGateEvaluator.IsAnonymouslyReadable(gates, gatedNodes, nodePath)
            ? Permission.Read
            : Permission.None;

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

        // 🚨 CLAIM ROLES ARE DELIBERATELY NOT FOLDED INTO NODE PERMISSIONS. They used to be —
        // AddClaimRoles(capturedContext/capturedCircuitContext) appended AccessContext.Roles to
        // roleIds RIGHT HERE, after the per-scope walk and after the deny subtraction. That made
        // every claim role a GLOBAL, UNDENIABLE grant on the entire mesh: the API token attaches
        // the user's DB/platform roles as claims, so a portal admin's `get` by exact path read
        // gated PAID course content they had never bought (memex, 2026-08-05 — 79,650 chars of
        // AgenticPrimerDe/02-CodeWunsch served to an unentitled caller), while `search` over the
        // SQL fold — which never sees claims — correctly denied the same node. The two read paths
        // disagreeing IS the paywall bypass.
        //
        // The access model (AGENTS.md, "Global admin"): a platform role grants the PLATFORM
        // gates, deliberately NOT cross-partition data access — it must not read a course it has
        // not bought. So node data permissions come from AccessAssignment nodes and policies
        // ONLY, matching the SQL path row for row. Claim roles keep their legitimate job at the
        // API-token capability check in GetEffectivePermissions (ClaimsCarryApi) — they decide
        // whether the token may use the API surface, never what data it may read.
        // Pinned by PaywallRealGateShapeTests (DbRoleClaim_DoesNotGrantNodeRead and siblings).
        return (roleIds, permissionCap, publicGrant);
    }

    /// <summary>
    /// True when the context's claim roles include a built-in role carrying
    /// <see cref="Permission.Api"/> (Viewer, Commenter, Editor, Admin, …). Backs the API-token
    /// capability gate — the only effect claim roles have on permission evaluation.
    /// </summary>
    private static bool ClaimsCarryApi(AccessContext ctx)
        => ctx.Roles is not null
           && ctx.Roles.Any(r =>
               BuiltInRolePerms.TryGetValue(r, out var rp) && rp.HasFlag(Permission.Api));

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
        IReadOnlySet<string> subjects,
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
            if (assignment == null || !subjects.Contains(assignment.AccessObject))
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
            ? $"path:{nsQuery} scope:children nodeType:{SecurityCollections.AccessAssignmentNodeType} select:path,id,namespace,name,nodeType,content"
            : $"namespace:{nsQuery} nodeType:{SecurityCollections.AccessAssignmentNodeType} select:path,id,namespace,name,nodeType,content";

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
            $"{nsFilter} nodeType:{SecurityCollections.PartitionAccessPolicyNodeType} select:path,id,namespace,name,nodeType,content");

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

    /// <summary>
    /// The instances of every gated node type, as <c>path → nodeType</c> — the set a target path is
    /// matched against to find its nearest gated ancestor. One process-wide cached query PER GATED
    /// TYPE (there is normally one), the same global shape <see cref="ObserveAllMembershipNodes"/>
    /// already uses, and bounded by the number of gated nodes rather than by their children.
    ///
    /// <para>🚨 Each per-type query is <c>StartWith</c>-seeded with an EMPTY list, and the fold
    /// below then starts from <paramref name="staticGatedNodes"/>. This observable feeds a
    /// <c>CombineLatest</c> that gates EVERY permission check, and CombineLatest emits nothing
    /// until every source has: a gated-type query that is slow (or that never matches because no
    /// such node exists yet) would otherwise stall the whole fold and hang every read. The empty
    /// seed also starts STRICTER — before a query answers, only the statically-known gated nodes
    /// are in the map, so an as-yet-unseen gated node has no public surface and is denied — which
    /// makes the pre-load window more restrictive, never a bypass.</para>
    /// </summary>
    private static IObservable<ImmutableDictionary<string, string>> ObserveGatedNodes(
        IMessageHub hub, IMeshNodeStreamCache cache, IReadOnlyList<NodeTypeGate> gates,
        ImmutableDictionary<string, string> staticGatedNodes)
    {
        if (gates.Count == 0)
            return Observable.Return(staticGatedNodes);

        var options = hub.JsonSerializerOptions;
        var perType = gates
            .Select(gate => cache
                .GetQuery($"{GatedQueryPrefix}{gate.NodeType}", options,
                    $"nodeType:{gate.NodeType} scope:subtree select:path,id,namespace,name,nodeType")
                .StartWith(Array.Empty<MeshNode>()))
            .ToArray();

        return Observable.CombineLatest(perType)
            .Select(lists =>
            {
                var map = staticGatedNodes;
                foreach (var list in lists)
                {
                    foreach (var node in list)
                    {
                        if (!string.IsNullOrEmpty(node.Path) && !string.IsNullOrEmpty(node.NodeType))
                            map = map.SetItem(node.Path, node.NodeType);
                    }
                }
                return map;
            });
    }

    private static IObservable<MeshNode[]> ObserveAllRoleNodes(IMessageHub hub)
    {
        var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        return cache.GetQuery(RoleQueryId, hub.JsonSerializerOptions, $"nodeType:{RoleNodeType} scope:subtree select:path,id,namespace,name,nodeType,content")
            .Select(arr => arr.ToArray());
    }

    /// <summary>
    /// Every <c>GroupMembership</c> node in the mesh, cached process-wide and read as System (like
    /// the other <c>$security-*</c> queries), so group access resolves GLOBALLY — a group and its
    /// members can live in a different partition than the grant (cross-partition licensing).
    /// </summary>
    private static IObservable<IEnumerable<MeshNode>> ObserveAllMembershipNodes(IMessageHub hub)
    {
        var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        return cache.GetQuery(MembershipQueryId, hub.JsonSerializerOptions,
            $"nodeType:{GroupMembershipNodeType} scope:subtree select:path,id,namespace,name,nodeType,content");
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

    /// <summary>
    /// The transitive set of groups <paramref name="userId"/> belongs to, expanded from all
    /// <c>GroupMembership</c> nodes. A membership's <c>Member</c> may itself be a group, so nested
    /// groups are followed (BFS). Never includes <paramref name="userId"/> itself.
    /// </summary>
    private static ImmutableHashSet<string> ResolveUserGroups(
        string userId, IEnumerable<MeshNode> memberships, JsonSerializerOptions options)
    {
        // member -> the groups it is DIRECTLY a member of.
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var node in memberships)
        {
            if (node.NodeType != GroupMembershipNodeType)
                continue;
            var m = DeserializeMembership(node, options);
            if (m is null || string.IsNullOrEmpty(m.Member) || m.Groups is null)
                continue;
            foreach (var entry in m.Groups)
            {
                if (string.IsNullOrEmpty(entry.Group))
                    continue;
                if (!edges.TryGetValue(m.Member, out var list))
                    edges[m.Member] = list = new List<string>();
                list.Add(entry.Group);
            }
        }

        var result = ImmutableHashSet<string>.Empty;
        if (edges.Count == 0)
            return result;
        var visited = new HashSet<string>(StringComparer.Ordinal) { userId };
        var queue = new Queue<string>();
        queue.Enqueue(userId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!edges.TryGetValue(current, out var groups))
                continue;
            foreach (var g in groups)
                if (visited.Add(g))   // a group may itself be a member of another group (nesting)
                {
                    result = result.Add(g);
                    queue.Enqueue(g);
                }
        }
        return result;
    }

    private static GroupMembership? DeserializeMembership(MeshNode node, JsonSerializerOptions options)
        => DeserializeContent<GroupMembership>(node, options);

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
    /// Snapshot equality for a scope's assignment list — by path AND CONTENT.
    ///
    /// <para>🚨 Path-only equality was a SILENT SWALLOW. The fold downstream
    /// (<see cref="ComputeScopeRoles"/> → <see cref="DeserializeAssignment"/>) is driven entirely by
    /// each node's CONTENT (<c>roles</c>, <c>denied</c>, <c>accessObject</c>). Comparing only the set
    /// of paths meant any emission that CORRECTED an assignment's content while the path set stayed
    /// the same was suppressed by the <c>DistinctUntilChanged</c> in
    /// <see cref="ObserveScopeAssignments"/> — permanently, not merely late. The subscriber kept the
    /// first snapshot forever, so a grant that arrived content-empty and was filled in a beat later
    /// never reached the evaluator. That is the <c>PaywallRealGateShapeTests</c> buyer-wait hang
    /// (reproduced 1-in-7 locally): the timeout could be raised to any value and it would still hang,
    /// because the correcting emission was discarded rather than delayed. It also meant EDITING an
    /// assignment in production (flip a role, set <c>denied</c>) was invisible to every live
    /// subscriber whose path set did not change.</para>
    ///
    /// <para>Erring toward "changed" is the SAFE direction: a false "different" costs one extra fold
    /// (and the final <c>DistinctUntilChanged()</c> on the resulting <see cref="Permission"/> stops it
    /// reaching subscribers), whereas a false "same" drops a permission change on the floor.</para>
    /// </summary>
    private sealed class MeshNodeListPathEquality : IEqualityComparer<IEnumerable<MeshNode>>
    {
        public static readonly MeshNodeListPathEquality Instance = new();

        public bool Equals(IEnumerable<MeshNode>? x, IEnumerable<MeshNode>? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            var xs = Signatures(x);
            var ys = Signatures(y);
            if (xs.Count != ys.Count) return false;
            foreach (var kvp in xs)
            {
                if (!ys.TryGetValue(kvp.Key, out var other)) return false;
                if (!string.Equals(kvp.Value, other, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static Dictionary<string, string> Signatures(IEnumerable<MeshNode> nodes)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var n in nodes)
            {
                if (string.IsNullOrEmpty(n.Path)) continue;
                map[n.Path] = ContentSignature(n.Content);
            }
            return map;
        }

        /// <summary>
        /// A stable string for the parts of an assignment the fold reads. JSON-shaped content is
        /// compared by its raw text; a typed <see cref="AccessAssignment"/> by the fields
        /// <see cref="ComputeScopeRoles"/> consumes (its <c>Roles</c> collection makes record value
        /// equality unreliable). Anything unrecognised falls back to <c>ToString()</c>, which at worst
        /// reports "changed" — the safe direction.
        /// </summary>
        private static string ContentSignature(object? content) => content switch
        {
            null => string.Empty,
            JsonElement je => je.GetRawText(),
            JsonNode jn => jn.ToJsonString(),
            AccessAssignment aa =>
                $"{aa.AccessObject}|{string.Join(",", aa.Roles.Select(r => $"{r.Role}:{r.Denied}"))}",
            var other => other.ToString() ?? string.Empty,
        };

        public int GetHashCode(IEnumerable<MeshNode> obj) => obj.Count();
    }

    /// <summary>
    /// Deserializes a node's content into <typeparamref name="TContent"/>, accepting every content
    /// shape a mesh emission can legally carry: an already-typed instance, a <see cref="JsonElement"/>
    /// (storage read), or a <see cref="JsonNode"/> (the AS-WRITTEN shape — application code builds
    /// content as <c>JsonObject</c>, and the change-notification entity supplement forwards it
    /// verbatim). 🚨 The <see cref="JsonNode"/> arm is load-bearing: without it, a raw-entity
    /// emission (issue #889 — the buyer's grant delivered via the pedestrian's notification
    /// supplement) silently deserialized to <c>null</c>, the grant folded to nothing, and
    /// permissions evaluated <see cref="Permission.None"/> even though the assignment was present
    /// and content-complete. <see cref="MeshNodeListPathEquality"/> already treats JsonNode as a
    /// first-class content shape — the deserializers must agree.
    /// </summary>
    private static TContent? DeserializeContent<TContent>(MeshNode node, JsonSerializerOptions options)
        where TContent : class
    {
        switch (node.Content)
        {
            case TContent typed:
                return typed;
            case JsonElement je:
                try { return JsonSerializer.Deserialize<TContent>(je.GetRawText(), options); }
                catch { return null; }
            case JsonNode jn:
                try { return JsonSerializer.Deserialize<TContent>(jn.ToJsonString(), options); }
                catch { return null; }
            default:
                return null;
        }
    }

    private static AccessAssignment? DeserializeAssignment(MeshNode node, JsonSerializerOptions options)
        => DeserializeContent<AccessAssignment>(node, options);

    private static PartitionAccessPolicy? DeserializePolicy(MeshNode node, JsonSerializerOptions options)
        => DeserializeContent<PartitionAccessPolicy>(node, options);

    private static Role? DeserializeRole(MeshNode node, JsonSerializerOptions options)
        => DeserializeContent<Role>(node, options);

    #endregion
}
