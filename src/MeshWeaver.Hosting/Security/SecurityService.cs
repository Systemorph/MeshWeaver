using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace MeshWeaver.Hosting.Security;

/// <summary>
/// Implementation of ISecurityService providing row-level security for mesh nodes.
/// </summary>
public class SecurityService : ISecurityService
{
    private readonly IPersistenceService _persistence;
    private readonly AccessService _accessService;
    private readonly SecurityStorageAdapter _storage;
    private readonly ILogger<SecurityService> _logger;

    // Built-in roles lookup
    private static readonly Dictionary<string, Role> BuiltInRoles = new()
    {
        { "Admin", Role.Admin },
        { "Editor", Role.Editor },
        { "Viewer", Role.Viewer }
    };

    public SecurityService(
        IPersistenceService persistence,
        AccessService accessService,
        SecurityStorageAdapter storage,
        ILogger<SecurityService> logger)
    {
        _persistence = persistence;
        _accessService = accessService;
        _storage = storage;
        _logger = logger;
    }

    #region Permission Evaluation

    public Task<bool> HasPermissionAsync(string nodePath, Permission permission, CancellationToken ct = default)
    {
        var context = _accessService.Context;
        var userId = context?.ObjectId;

        // If no user context, check if anonymous access is allowed
        if (string.IsNullOrEmpty(userId))
            return HasAnonymousPermissionAsync(nodePath, permission, ct);

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

        // If no user context, return anonymous permissions
        if (string.IsNullOrEmpty(userId))
            return GetAnonymousPermissionsAsync(nodePath, ct);

        return GetEffectivePermissionsAsync(nodePath, userId, ct);
    }

    public async Task<Permission> GetEffectivePermissionsAsync(string nodePath, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId))
            return await GetAnonymousPermissionsAsync(nodePath, ct);

        // 1. Get the node to determine its NodeType
        var node = await _persistence.GetNodeAsync(nodePath, ct);
        var nodeType = node?.NodeType;

        // 2. Get role configuration for NodeType
        RoleConfiguration? roleConfig = null;
        if (!string.IsNullOrEmpty(nodeType))
        {
            roleConfig = await _storage.GetRoleConfigurationAsync(nodeType, ct);
        }

        // 3. Get node-specific security configuration
        var nodeSecurityConfig = await _storage.GetNodeSecurityConfigurationAsync(nodePath, ct);

        // 4. Determine inheritance setting
        var inheritFromParent = nodeSecurityConfig?.InheritFromParentOverride
            ?? roleConfig?.InheritFromParent
            ?? true;

        // 5. Build path hierarchy if inheritance enabled
        var pathsToCheck = GetPathHierarchy(nodePath, inheritFromParent);

        // 6. Collect effective permissions from role assignments
        var effectivePermissions = Permission.None;

        await foreach (var assignment in _storage.GetUserRoleAssignmentsAsync(userId, ct))
        {
            // Check if assignment applies to this path
            if (assignment.NodePath == null || pathsToCheck.Contains(assignment.NodePath))
            {
                var role = await GetRoleAsync(assignment.RoleId, ct);
                if (role != null)
                {
                    // Check if role is inheritable or if this is the exact node
                    if (role.IsInheritable || assignment.NodePath == nodePath || assignment.NodePath == null)
                    {
                        effectivePermissions |= role.Permissions;
                    }
                }
            }
        }

        // 7. Add permissions from AccessContext.Roles (claim-based roles)
        var context = _accessService.Context;
        if (context?.Roles != null)
        {
            foreach (var roleName in context.Roles)
            {
                var role = await GetRoleAsync(roleName, ct);
                if (role != null)
                {
                    effectivePermissions |= role.Permissions;
                }
            }
        }

        _logger.LogTrace("User {UserId} has permissions {Permissions} on node {NodePath}",
            userId, effectivePermissions, nodePath);

        return effectivePermissions;
    }

    private async Task<bool> HasAnonymousPermissionAsync(string nodePath, Permission permission, CancellationToken ct)
    {
        var anonymousPermissions = await GetAnonymousPermissionsAsync(nodePath, ct);
        return anonymousPermissions.HasFlag(permission);
    }

    private async Task<Permission> GetAnonymousPermissionsAsync(string nodePath, CancellationToken ct)
    {
        // Get the node to determine its NodeType
        var node = await _persistence.GetNodeAsync(nodePath, ct);
        var nodeType = node?.NodeType;

        // Get node-specific security configuration
        var nodeSecurityConfig = await _storage.GetNodeSecurityConfigurationAsync(nodePath, ct);

        // Check node-level override first
        if (nodeSecurityConfig?.IsPublicOverride == true)
        {
            return nodeSecurityConfig.AnonymousPermissionsOverride ?? Permission.Read;
        }

        if (nodeSecurityConfig?.IsPublicOverride == false)
        {
            return Permission.None;
        }

        // Check NodeType configuration
        if (!string.IsNullOrEmpty(nodeType))
        {
            var roleConfig = await _storage.GetRoleConfigurationAsync(nodeType, ct);
            if (roleConfig != null)
            {
                if (roleConfig.IsPublic)
                {
                    return roleConfig.AnonymousPermissions != Permission.None
                        ? roleConfig.AnonymousPermissions
                        : Permission.Read;
                }
                return Permission.None;
            }
        }

        // Default: no anonymous access
        return Permission.None;
    }

    private static List<string> GetPathHierarchy(string nodePath, bool includeAncestors)
    {
        var paths = new List<string> { nodePath };

        if (includeAncestors && !string.IsNullOrEmpty(nodePath))
        {
            var segments = nodePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = segments.Length - 1; i > 0; i--)
            {
                paths.Add(string.Join("/", segments.Take(i)));
            }
        }

        return paths;
    }

    #endregion

    #region Role Configuration (NodeType Level)

    public Task<RoleConfiguration?> GetRoleConfigurationAsync(string nodeType, CancellationToken ct = default)
    {
        return _storage.GetRoleConfigurationAsync(nodeType, ct);
    }

    public Task SetRoleConfigurationAsync(RoleConfiguration config, CancellationToken ct = default)
    {
        return _storage.SaveRoleConfigurationAsync(config, ct);
    }

    #endregion

    #region Node Security Configuration (Instance Level)

    public Task<NodeSecurityConfiguration?> GetNodeSecurityConfigurationAsync(string nodePath, CancellationToken ct = default)
    {
        return _storage.GetNodeSecurityConfigurationAsync(nodePath, ct);
    }

    public Task SetNodeSecurityConfigurationAsync(NodeSecurityConfiguration config, CancellationToken ct = default)
    {
        return _storage.SaveNodeSecurityConfigurationAsync(config, ct);
    }

    #endregion

    #region Role Assignments

    public async Task AssignRoleAsync(string userId, string roleId, string? nodePath, string? assignedBy = null, CancellationToken ct = default)
    {
        var assignment = new RoleAssignment
        {
            UserId = userId,
            RoleId = roleId,
            NodePath = nodePath,
            CreatedBy = assignedBy,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _storage.SaveRoleAssignmentAsync(assignment, ct);

        _logger.LogInformation("Assigned role {RoleId} to user {UserId} on node {NodePath} by {AssignedBy}",
            roleId, userId, nodePath ?? "(global)", assignedBy ?? "(system)");
    }

    public async Task RemoveRoleAssignmentAsync(string userId, string roleId, string? nodePath, CancellationToken ct = default)
    {
        await _storage.RemoveRoleAssignmentAsync(userId, roleId, nodePath, ct);

        _logger.LogInformation("Removed role {RoleId} from user {UserId} on node {NodePath}",
            roleId, userId, nodePath ?? "(global)");
    }

    public IAsyncEnumerable<RoleAssignment> GetUserRoleAssignmentsAsync(string userId, CancellationToken ct = default)
    {
        return _storage.GetUserRoleAssignmentsAsync(userId, ct);
    }

    public IAsyncEnumerable<RoleAssignment> GetNodeRoleAssignmentsAsync(string nodePath, CancellationToken ct = default)
    {
        return _storage.GetNodeRoleAssignmentsAsync(nodePath, ct);
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

        // Check custom roles
        return await _storage.GetCustomRoleAsync(roleId, ct);
    }

    public async IAsyncEnumerable<Role> GetRolesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        // Return built-in roles first
        foreach (var role in BuiltInRoles.Values)
        {
            yield return role;
        }

        // Return custom roles
        await foreach (var role in _storage.GetCustomRolesAsync(ct))
        {
            yield return role;
        }
    }

    public Task SaveRoleAsync(Role role, CancellationToken ct = default)
    {
        // Don't allow overwriting built-in roles
        if (BuiltInRoles.ContainsKey(role.Id))
        {
            throw new InvalidOperationException($"Cannot modify built-in role: {role.Id}");
        }

        return _storage.SaveCustomRoleAsync(role, ct);
    }

    #endregion
}
