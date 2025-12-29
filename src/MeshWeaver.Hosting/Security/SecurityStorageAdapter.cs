using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace MeshWeaver.Hosting.Security;

/// <summary>
/// Adapter for storing security data using IPersistenceService partitions.
/// Stores data in dedicated _security namespace:
/// - _security/roles/{NodeType} - Role configurations
/// - _security/nodes/{sanitizedPath} - Node security configurations
/// - _security/assignments - Role assignments
/// - _security/role-definitions - Custom role definitions
/// </summary>
public class SecurityStorageAdapter
{
    private const string SecurityNamespace = "_security";
    private const string RoleConfigPath = "roles";
    private const string NodeSecurityPath = "nodes";
    private const string AssignmentsPath = "assignments";
    private const string RoleDefinitionsPath = "role-definitions";

    private readonly IPersistenceService _persistence;

    // In-memory caches for performance
    private readonly ConcurrentDictionary<string, RoleConfiguration> _roleConfigCache = new();
    private readonly ConcurrentDictionary<string, NodeSecurityConfiguration> _nodeSecurityCache = new();
    private readonly ConcurrentDictionary<string, Role> _customRoleCache = new();
    private List<RoleAssignment>? _assignmentsCache;
    private readonly SemaphoreSlim _assignmentsLock = new(1, 1);

    public SecurityStorageAdapter(IPersistenceService persistence)
    {
        _persistence = persistence;
    }

    #region Role Configurations

    public async Task<RoleConfiguration?> GetRoleConfigurationAsync(string nodeType, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(nodeType))
            return null;

        if (_roleConfigCache.TryGetValue(nodeType, out var cached))
            return cached;

        var path = $"{SecurityNamespace}/{RoleConfigPath}";
        var subPath = SanitizePath(nodeType);

        await foreach (var obj in _persistence.GetPartitionObjectsAsync(path, subPath).WithCancellation(ct))
        {
            if (obj is RoleConfiguration config && config.NodeType == nodeType)
            {
                _roleConfigCache[nodeType] = config;
                return config;
            }
        }

        return null;
    }

    public async Task SaveRoleConfigurationAsync(RoleConfiguration config, CancellationToken ct = default)
    {
        var path = $"{SecurityNamespace}/{RoleConfigPath}";
        var subPath = SanitizePath(config.NodeType);

        await _persistence.SavePartitionObjectsAsync(path, subPath, [config], ct);
        _roleConfigCache[config.NodeType] = config;
    }

    #endregion

    #region Node Security Configurations

    public async Task<NodeSecurityConfiguration?> GetNodeSecurityConfigurationAsync(string nodePath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(nodePath))
            return null;

        if (_nodeSecurityCache.TryGetValue(nodePath, out var cached))
            return cached;

        var path = $"{SecurityNamespace}/{NodeSecurityPath}";
        var subPath = SanitizePath(nodePath);

        await foreach (var obj in _persistence.GetPartitionObjectsAsync(path, subPath).WithCancellation(ct))
        {
            if (obj is NodeSecurityConfiguration config && config.NodePath == nodePath)
            {
                _nodeSecurityCache[nodePath] = config;
                return config;
            }
        }

        return null;
    }

    public async Task SaveNodeSecurityConfigurationAsync(NodeSecurityConfiguration config, CancellationToken ct = default)
    {
        var path = $"{SecurityNamespace}/{NodeSecurityPath}";
        var subPath = SanitizePath(config.NodePath);

        await _persistence.SavePartitionObjectsAsync(path, subPath, [config], ct);
        _nodeSecurityCache[config.NodePath] = config;
    }

    #endregion

    #region Role Assignments

    public async IAsyncEnumerable<RoleAssignment> GetAllRoleAssignmentsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await _assignmentsLock.WaitAsync(ct);
        try
        {
            if (_assignmentsCache == null)
            {
                _assignmentsCache = [];
                var path = $"{SecurityNamespace}/{AssignmentsPath}";

                await foreach (var obj in _persistence.GetPartitionObjectsAsync(path, null).WithCancellation(ct))
                {
                    if (obj is RoleAssignment assignment)
                    {
                        _assignmentsCache.Add(assignment);
                    }
                }
            }

            foreach (var assignment in _assignmentsCache)
            {
                yield return assignment;
            }
        }
        finally
        {
            _assignmentsLock.Release();
        }
    }

    public async IAsyncEnumerable<RoleAssignment> GetUserRoleAssignmentsAsync(string userId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var assignment in GetAllRoleAssignmentsAsync(ct))
        {
            if (assignment.UserId == userId)
                yield return assignment;
        }
    }

    public async IAsyncEnumerable<RoleAssignment> GetNodeRoleAssignmentsAsync(string nodePath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var assignment in GetAllRoleAssignmentsAsync(ct))
        {
            if (assignment.NodePath == nodePath)
                yield return assignment;
        }
    }

    public async Task SaveRoleAssignmentAsync(RoleAssignment assignment, CancellationToken ct = default)
    {
        await _assignmentsLock.WaitAsync(ct);
        try
        {
            // Load existing assignments if not cached
            if (_assignmentsCache == null)
            {
                _assignmentsCache = [];
                var path = $"{SecurityNamespace}/{AssignmentsPath}";

                await foreach (var obj in _persistence.GetPartitionObjectsAsync(path, null).WithCancellation(ct))
                {
                    if (obj is RoleAssignment a)
                        _assignmentsCache.Add(a);
                }
            }

            // Remove existing assignment with same user/role/path
            _assignmentsCache.RemoveAll(a =>
                a.UserId == assignment.UserId &&
                a.RoleId == assignment.RoleId &&
                a.NodePath == assignment.NodePath);

            // Add new assignment
            _assignmentsCache.Add(assignment);

            // Save all assignments
            var persistPath = $"{SecurityNamespace}/{AssignmentsPath}";
            await _persistence.SavePartitionObjectsAsync(persistPath, null, _assignmentsCache.Cast<object>().ToList(), ct);
        }
        finally
        {
            _assignmentsLock.Release();
        }
    }

    public async Task RemoveRoleAssignmentAsync(string userId, string roleId, string? nodePath, CancellationToken ct = default)
    {
        await _assignmentsLock.WaitAsync(ct);
        try
        {
            // Load existing assignments if not cached
            if (_assignmentsCache == null)
            {
                _assignmentsCache = [];
                var path = $"{SecurityNamespace}/{AssignmentsPath}";

                await foreach (var obj in _persistence.GetPartitionObjectsAsync(path, null).WithCancellation(ct))
                {
                    if (obj is RoleAssignment a)
                        _assignmentsCache.Add(a);
                }
            }

            // Remove matching assignment
            var removed = _assignmentsCache.RemoveAll(a =>
                a.UserId == userId &&
                a.RoleId == roleId &&
                a.NodePath == nodePath);

            if (removed > 0)
            {
                // Save all assignments
                var persistPath = $"{SecurityNamespace}/{AssignmentsPath}";
                await _persistence.SavePartitionObjectsAsync(persistPath, null, _assignmentsCache.Cast<object>().ToList(), ct);
            }
        }
        finally
        {
            _assignmentsLock.Release();
        }
    }

    #endregion

    #region Custom Role Definitions

    public async Task<Role?> GetCustomRoleAsync(string roleId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(roleId))
            return null;

        if (_customRoleCache.TryGetValue(roleId, out var cached))
            return cached;

        var path = $"{SecurityNamespace}/{RoleDefinitionsPath}";

        await foreach (var obj in _persistence.GetPartitionObjectsAsync(path, null).WithCancellation(ct))
        {
            if (obj is Role role)
            {
                _customRoleCache[role.Id] = role;
                if (role.Id == roleId)
                    return role;
            }
        }

        return null;
    }

    public async IAsyncEnumerable<Role> GetCustomRolesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = $"{SecurityNamespace}/{RoleDefinitionsPath}";

        await foreach (var obj in _persistence.GetPartitionObjectsAsync(path, null).WithCancellation(ct))
        {
            if (obj is Role role)
            {
                _customRoleCache[role.Id] = role;
                yield return role;
            }
        }
    }

    public async Task SaveCustomRoleAsync(Role role, CancellationToken ct = default)
    {
        // Load existing roles
        var roles = new List<Role>();
        var path = $"{SecurityNamespace}/{RoleDefinitionsPath}";

        await foreach (var obj in _persistence.GetPartitionObjectsAsync(path, null).WithCancellation(ct))
        {
            if (obj is Role r && r.Id != role.Id)
                roles.Add(r);
        }

        roles.Add(role);

        await _persistence.SavePartitionObjectsAsync(path, null, roles.Cast<object>().ToList(), ct);
        _customRoleCache[role.Id] = role;
    }

    #endregion

    /// <summary>
    /// Sanitizes a path for use as a sub-path in partition storage.
    /// Replaces slashes with underscores to avoid path separator issues.
    /// </summary>
    private static string SanitizePath(string path)
        => path.Replace("/", "_").Replace("\\", "_");
}
