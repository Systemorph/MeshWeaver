using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
/// - AccessAssignment MeshNodes: namespace = scope, id = SubjectId, content has Roles[] array
/// - Permission evaluation walks AccessAssignment nodes from root to target path.
/// - Built-in roles: Admin, Editor, Viewer, Commenter.
/// </summary>
public class SecurityService : ISecurityService
{
    private const string AccessPartitionName = "Access";

    private readonly IPersistenceService _persistence;
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
        { "Commenter", Role.Commenter }
    };

    // Cache for custom roles
    private readonly ConcurrentDictionary<string, Role> _customRoleCache = new();
    private bool _customRolesLoaded;

    public SecurityService(
        IPersistenceService persistence,
        AccessService accessService,
        IMessageHub hub,
        ILogger<SecurityService> logger)
    {
        _persistence = persistence;
        _accessService = accessService;
        _hub = hub;
        _logger = logger;
    }

    #region Permission Evaluation

    public Task<bool> HasPermissionAsync(string nodePath, Permission permission, CancellationToken ct = default)
    {
        var context = _accessService.Context;
        var userId = context?.ObjectId;

        // If no user context, check "Public" user permissions
        if (string.IsNullOrEmpty(userId))
            userId = WellKnownUsers.Public;

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

        if (string.IsNullOrEmpty(userId))
            userId = WellKnownUsers.Public;

        return GetEffectivePermissionsAsync(nodePath, userId, ct);
    }

    public async Task<Permission> GetEffectivePermissionsAsync(string nodePath, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId))
            userId = WellKnownUsers.Public;

        var cacheKey = $"{userId}:{nodePath}";
        if (_permissionCache.TryGetValue(cacheKey, out Permission cached))
            return cached;

        var effectivePermissions = Permission.None;

        // Collect role assignments from AccessAssignment MeshNodes across all ancestor scopes + global
        // Walk from root to target path, applying closest-wins semantics per role
        var roleAssignments = new Dictionary<string, (bool Denied, int Depth)>();

        // Check all scopes: global (""), then each ancestor level, then self
        var scopes = GetScopeHierarchy(nodePath);
        for (var i = 0; i < scopes.Count; i++)
        {
            var scope = scopes[i];
            var depth = i; // deeper = higher index

            await foreach (var node in _persistence.GetChildrenAsync(scope).WithCancellation(ct))
            {
                if (node.NodeType != "AccessAssignment" || node.Content == null)
                    continue;

                var assignment = DeserializeAssignment(node);
                if (assignment == null || assignment.SubjectId != userId)
                    continue;

                // Process each role in the assignment's Roles list
                foreach (var roleAssignment in assignment.Roles)
                {
                    if (string.IsNullOrEmpty(roleAssignment.RoleId))
                        continue;

                    // Closest-wins: later (deeper) assignments override earlier ones for the same role
                    roleAssignments[roleAssignment.RoleId] = (roleAssignment.Denied, depth);
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

        _logger.LogTrace("User {UserId} has permissions {Permissions} on node {NodePath}",
            userId, effectivePermissions, nodePath);

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
                return System.Text.Json.JsonSerializer.Deserialize<AccessAssignment>(je.GetRawText(), _hub.JsonSerializerOptions);
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
        await foreach (var obj in _persistence.GetPartitionObjectsAsync(AccessPartitionName, null).WithCancellation(ct))
        {
            if (obj is Role existing && existing.Id == role.Id)
                continue;
            objects.Add(obj);
        }

        objects.Add(role);
        await _persistence.SavePartitionObjectsAsync(AccessPartitionName, null, objects, ct);
        _customRoleCache[role.Id] = role;
    }

    private async Task LoadCustomRolesAsync(CancellationToken ct)
    {
        if (_customRolesLoaded)
            return;

        await foreach (var obj in _persistence.GetPartitionObjectsAsync(AccessPartitionName, null).WithCancellation(ct))
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
        var existingNode = await _persistence.GetNodeAsync(path, ct);
        var existingAssignment = existingNode != null ? DeserializeAssignment(existingNode) : null;

        var roles = existingAssignment?.Roles?.ToList() ?? [];

        // Add role if not already present
        if (!roles.Any(r => r.RoleId == roleId))
            roles.Add(new RoleAssignment { RoleId = roleId });

        var node = new MeshNode(nodeId, ns)
        {
            NodeType = "AccessAssignment",
            Name = $"{userId} Access",
            Content = new AccessAssignment
            {
                SubjectId = userId,
                DisplayName = existingAssignment?.DisplayName ?? userId,
                Roles = roles
            }
        };

        await _persistence.SaveNodeAsync(node, ct);
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

        var existingNode = await _persistence.GetNodeAsync(path, ct);
        var existingAssignment = existingNode != null ? DeserializeAssignment(existingNode) : null;

        if (existingAssignment == null)
            return; // Nothing to remove

        var roles = existingAssignment.Roles.Where(r => r.RoleId != roleId).ToList();

        if (roles.Count == 0)
        {
            // No roles left, delete the node
            await _persistence.DeleteNodeAsync(path, false, ct);
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
                    SubjectId = userId,
                    DisplayName = existingAssignment.DisplayName,
                    Roles = roles
                }
            };
            await _persistence.SaveNodeAsync(node, ct);
        }

        ClearPermissionCache();
    }

    /// <summary>
    /// Clears the permission cache (useful for testing or after bulk updates).
    /// </summary>
    public void ClearPermissionCache()
    {
        (_permissionCache as MemoryCache)?.Clear();
    }

    #endregion
}
