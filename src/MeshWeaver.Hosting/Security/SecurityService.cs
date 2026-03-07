using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Caching.Memory;
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
    private const string AccessPartitionName = "Access";

    private readonly IPersistenceServiceCore _persistenceCore;
    private readonly AccessService _accessService;
    private readonly ILogger<SecurityService> _logger;
    private readonly IMessageHub _hub;

    // Permission result cache (keyed by "userId:nodePath")
    private readonly IMemoryCache _permissionCache = new MemoryCache(new MemoryCacheOptions());
    private static readonly MemoryCacheEntryOptions PermissionCacheOptions = new() { SlidingExpiration = TimeSpan.FromMinutes(5) };

    // Built-in roles lookup
    private static readonly Dictionary<string, Role> BuiltInRoles = new()
    {
        { "Admin", Role.Admin },
        { "Editor", Role.Editor },
        { "Viewer", Role.Viewer },
        { "Commenter", Role.Commenter },
        { "PlatformAdmin", Role.PlatformAdmin }
    };

    // Cache for custom roles
    private readonly ConcurrentDictionary<string, Role> _customRoleCache = new();
    private bool _customRolesLoaded;

    // Cache for partition access policies (keyed by namespace)
    private readonly ConcurrentDictionary<string, PartitionAccessPolicy?> _policyCache = new();

    // Static policies from IStaticNodeProvider (e.g., Doc, Agent, Role namespaces are read-only)
    private readonly Dictionary<string, PartitionAccessPolicy> _staticPolicies;

    public SecurityService(
        IPersistenceServiceCore persistenceCore,
        AccessService accessService,
        IMessageHub hub,
        ILogger<SecurityService> logger,
        IEnumerable<IStaticNodeProvider> staticNodeProviders)
    {
        _persistenceCore = persistenceCore;
        _accessService = accessService;
        _hub = hub;
        _logger = logger;

        // Collect PartitionAccessPolicy nodes from static providers
        _staticPolicies = staticNodeProviders
            .SelectMany(p => p.GetStaticNodes())
            .Where(n => n.NodeType == "PartitionAccessPolicy" && n.Id == "_Policy" && n.Content is PartitionAccessPolicy)
            .ToDictionary(n => n.Namespace ?? "", n => (PartitionAccessPolicy)n.Content!);
    }

    private JsonSerializerOptions Options => _hub.JsonSerializerOptions;

    #region Permission Evaluation

    public Task<bool> HasPermissionAsync(string nodePath, Permission permission, CancellationToken ct = default)
    {
        var context = _accessService.Context;
        var userId = context?.ObjectId;

        // If no user context or virtual user, check "Anonymous" user permissions
        if (string.IsNullOrEmpty(userId) || context?.IsVirtual == true)
            userId = WellKnownUsers.Anonymous;

        return HasPermissionAsync(nodePath, userId, permission, ct);
    }

    public async Task<bool> HasPermissionAsync(string nodePath, string userId, Permission permission, CancellationToken ct = default)
    {
        if (permission == Permission.None)
            return true;

        var effectivePermissions = await GetEffectivePermissionsAsync(nodePath, userId, ct);
        return effectivePermissions.HasFlag(permission);
    }

    public Task<Permission> GetEffectivePermissionsAsync(string nodePath, CancellationToken ct = default)
    {
        var context = _accessService.Context;
        var userId = context?.ObjectId;

        // If no user context or virtual user, check "Anonymous" user permissions
        if (string.IsNullOrEmpty(userId) || context?.IsVirtual == true)
            userId = WellKnownUsers.Anonymous;

        return GetEffectivePermissionsAsync(nodePath, userId, ct);
    }

    public async Task<Permission> GetEffectivePermissionsAsync(string nodePath, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId))
            userId = WellKnownUsers.Anonymous;

        var cacheKey = $"{userId}:{nodePath}";
        if (_permissionCache.TryGetValue(cacheKey, out Permission cached))
            return cached;

        var effectivePermissions = Permission.None;

        // Collect role assignments from AccessAssignment MeshNodes across all ancestor scopes + global
        // Walk from root to target path, applying closest-wins semantics per role
        var roleAssignments = new Dictionary<string, (bool Denied, int Depth)>();

        // Track accumulated permission cap from PartitionAccessPolicy nodes
        var permissionCap = Permission.All;

        // Check all scopes: global (""), then each ancestor level, then self
        var scopes = GetScopeHierarchy(nodePath);
        for (var i = 0; i < scopes.Count; i++)
        {
            var scope = scopes[i];
            var depth = i; // deeper = higher index

            // Check static policies from IStaticNodeProvider (e.g., Doc, Agent, Role are read-only)
            if (_staticPolicies.TryGetValue(scope, out var staticPolicy))
            {
                if (staticPolicy.BreaksInheritance)
                    roleAssignments.Clear();
                permissionCap &= staticPolicy.GetPermissionCap();
            }

            await foreach (var node in _persistenceCore.GetChildrenAsync(scope, Options).WithCancellation(ct))
            {
                if (node.Content == null)
                    continue;

                // Check for PartitionAccessPolicy nodes (persisted policies)
                if (node.NodeType == "PartitionAccessPolicy" && node.Id == "_Policy")
                {
                    var policy = DeserializePolicy(node);
                    if (policy != null)
                    {
                        if (policy.BreaksInheritance)
                            roleAssignments.Clear();

                        // Tighten the cap (nested policies can only further restrict)
                        permissionCap &= policy.GetPermissionCap();
                    }
                    continue;
                }

                if (node.NodeType != "AccessAssignment")
                    continue;

                var assignment = DeserializeAssignment(node);
                if (assignment == null || assignment.AccessObject != userId)
                    continue;

                // Process each role in the assignment's Roles list
                foreach (var roleAssignment in assignment.Roles)
                {
                    if (string.IsNullOrEmpty(roleAssignment.Role))
                        continue;

                    // Closest-wins: later (deeper) assignments override earlier ones for the same role
                    roleAssignments[roleAssignment.Role] = (roleAssignment.Denied, depth);
                }
            }
        }

        // Resolve permissions from non-denied role assignments
        foreach (var (roleId, (denied, _)) in roleAssignments)
        {
            if (denied)
                continue;

            var role = await GetRoleAsync(roleId, ct);
            if (role != null)
                effectivePermissions |= role.Permissions;
        }

        // Add permissions from AccessContext.Roles (claim-based roles)
        var context = _accessService.Context;
        if (context?.Roles != null
            && !string.IsNullOrEmpty(context.ObjectId)
            && context.ObjectId == userId)
        {
            foreach (var roleName in context.Roles)
            {
                var role = await GetRoleAsync(roleName, ct);
                if (role != null)
                    effectivePermissions |= role.Permissions;
            }
        }

        // For authenticated (non-anonymous, non-public) users, merge in Public permissions.
        // Public represents the baseline permission floor for all logged-in users.
        if (userId != WellKnownUsers.Anonymous && userId != WellKnownUsers.Public)
        {
            var publicPermissions = await GetEffectivePermissionsAsync(nodePath, WellKnownUsers.Public, ct);
            effectivePermissions |= publicPermissions;
        }

        // Apply the permission cap as final mask from PartitionAccessPolicy nodes
        effectivePermissions &= permissionCap;

        _logger.LogTrace("User {UserId} has permissions {Permissions} on node {NodePath} (cap: {Cap})",
            userId, effectivePermissions, nodePath, permissionCap);

        _permissionCache.Set(cacheKey, effectivePermissions, PermissionCacheOptions);
        return effectivePermissions;
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

    public async Task<Role?> GetRoleAsync(string roleId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(roleId))
            return null;

        // Check built-in roles first
        if (BuiltInRoles.TryGetValue(roleId, out var builtInRole))
            return builtInRole;

        // Check custom roles cache
        if (_customRoleCache.TryGetValue(roleId, out var cachedRole))
            return cachedRole;

        // Load all custom roles from global Access partition
        await LoadCustomRolesAsync(ct);
        return _customRoleCache.GetValueOrDefault(roleId);
    }

    public async IAsyncEnumerable<Role> GetRolesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var role in BuiltInRoles.Values)
        {
            yield return role;
        }

        await LoadCustomRolesAsync(ct);
        foreach (var role in _customRoleCache.Values)
        {
            yield return role;
        }
    }

    public async Task SaveRoleAsync(Role role, CancellationToken ct = default)
    {
        if (BuiltInRoles.ContainsKey(role.Id))
            throw new InvalidOperationException($"Cannot modify built-in role: {role.Id}");

        var objects = new List<object>();
        await foreach (var obj in _persistenceCore.GetPartitionObjectsAsync(AccessPartitionName, null, Options).WithCancellation(ct))
        {
            if (obj is Role existing && existing.Id == role.Id)
                continue;
            objects.Add(obj);
        }

        objects.Add(role);
        await _persistenceCore.SavePartitionObjectsAsync(AccessPartitionName, null, objects, Options, ct);
        _customRoleCache[role.Id] = role;
    }

    private async Task LoadCustomRolesAsync(CancellationToken ct)
    {
        if (_customRolesLoaded)
            return;

        await foreach (var obj in _persistenceCore.GetPartitionObjectsAsync(AccessPartitionName, null, Options).WithCancellation(ct))
        {
            if (obj is Role role && !BuiltInRoles.ContainsKey(role.Id))
                _customRoleCache[role.Id] = role;
        }
        _customRolesLoaded = true;
    }

    #endregion

    #region Access Assignment Management

    /// <summary>
    /// Adds a role to a user's AccessAssignment MeshNode.
    /// If the node already exists, appends the role. If not, creates a new node.
    /// Node: namespace = targetNamespace ?? "", id = {userId}_Access, nodeType = "AccessAssignment"
    /// </summary>
    public async Task AddUserRoleAsync(string userId, string roleId, string? targetNamespace, string? assignedBy = null, CancellationToken ct = default)
    {
        _logger.LogInformation("Adding role {RoleId} to user {UserId} on namespace {Namespace} by {AssignedBy}",
            roleId, userId, targetNamespace ?? "(global)", assignedBy ?? "(system)");

        var ns = targetNamespace ?? "";
        var nodeId = $"{userId}_Access";
        var path = string.IsNullOrEmpty(ns) ? nodeId : $"{ns}/{nodeId}";

        // Try to load existing AccessAssignment node
        var existingNode = await _persistenceCore.GetNodeAsync(path, Options, ct);
        var existingAssignment = existingNode != null ? DeserializeAssignment(existingNode) : null;

        var roles = existingAssignment?.Roles?.ToList() ?? [];

        // Add role if not already present
        if (!roles.Any(r => r.Role == roleId))
            roles.Add(new RoleAssignment { Role = roleId });

        var node = new MeshNode(nodeId, ns)
        {
            NodeType = "AccessAssignment",
            Name = $"{userId} Access",
            Content = new AccessAssignment
            {
                AccessObject = userId,
                DisplayName = existingAssignment?.DisplayName ?? userId,
                Roles = roles
            }
        };

        await _persistenceCore.SaveNodeAsync(node, Options, ct);
        ClearPermissionCache();
    }

    /// <summary>
    /// Removes a role from a user's AccessAssignment MeshNode.
    /// If the node has no remaining roles, deletes the node.
    /// </summary>
    public async Task RemoveUserRoleAsync(string userId, string roleId, string? targetNamespace, CancellationToken ct = default)
    {
        _logger.LogInformation("Removing role {RoleId} from user {UserId} on namespace {Namespace}",
            roleId, userId, targetNamespace ?? "(global)");

        var ns = targetNamespace ?? "";
        var nodeId = $"{userId}_Access";
        var path = string.IsNullOrEmpty(ns) ? nodeId : $"{ns}/{nodeId}";

        var existingNode = await _persistenceCore.GetNodeAsync(path, Options, ct);
        var existingAssignment = existingNode != null ? DeserializeAssignment(existingNode) : null;

        if (existingAssignment == null)
            return; // Nothing to remove

        var roles = existingAssignment.Roles.Where(r => r.Role != roleId).ToList();

        if (roles.Count == 0)
        {
            // No roles left, delete the node
            await _persistenceCore.DeleteNodeAsync(path, false, ct);
        }
        else
        {
            // Update with remaining roles
            var node = new MeshNode(nodeId, ns)
            {
                NodeType = "AccessAssignment",
                Name = $"{userId} Access",
                Content = new AccessAssignment
                {
                    AccessObject = userId,
                    DisplayName = existingAssignment.DisplayName,
                    Roles = roles
                }
            };
            await _persistenceCore.SaveNodeAsync(node, Options, ct);
        }

        ClearPermissionCache();
    }

    /// <summary>
    /// Clears the permission cache (useful for testing or after bulk updates).
    /// </summary>
    public void ClearPermissionCache()
    {
        (_permissionCache as MemoryCache)?.Clear();
        _policyCache.Clear();
    }

    #endregion

    #region Partition Access Policies

    /// <inheritdoc />
    public async Task<PartitionAccessPolicy?> GetPolicyAsync(string targetNamespace, CancellationToken ct = default)
    {
        var ns = targetNamespace ?? "";
        if (_policyCache.TryGetValue(ns, out var cached))
            return cached;

        var path = string.IsNullOrEmpty(ns) ? "_Policy" : $"{ns}/_Policy";
        var node = await _persistenceCore.GetNodeAsync(path, Options, ct);
        var policy = node != null ? DeserializePolicy(node) : null;
        _policyCache[ns] = policy;
        return policy;
    }

    /// <inheritdoc />
    public async Task SetPolicyAsync(string targetNamespace, PartitionAccessPolicy policy, CancellationToken ct = default)
    {
        var ns = targetNamespace ?? "";

        _logger.LogInformation("Setting partition access policy on namespace {Namespace}: PermissionCap={PermissionCap}, BreaksInheritance={BreaksInheritance}",
            string.IsNullOrEmpty(ns) ? "(global)" : ns, policy.GetPermissionCap(), policy.BreaksInheritance);

        var node = new MeshNode("_Policy", ns)
        {
            NodeType = "PartitionAccessPolicy",
            Name = "Access Policy",
            Content = policy
        };

        await _persistenceCore.SaveNodeAsync(node, Options, ct);
        _policyCache[ns] = policy;
        ClearPermissionCache();
    }

    /// <inheritdoc />
    public async Task RemovePolicyAsync(string targetNamespace, CancellationToken ct = default)
    {
        var ns = targetNamespace ?? "";

        _logger.LogInformation("Removing partition access policy from namespace {Namespace}",
            string.IsNullOrEmpty(ns) ? "(global)" : ns);

        var path = string.IsNullOrEmpty(ns) ? "_Policy" : $"{ns}/_Policy";
        await _persistenceCore.DeleteNodeAsync(path, false, ct);
        _policyCache.TryRemove(ns, out _);
        ClearPermissionCache();
    }

    #endregion
}
