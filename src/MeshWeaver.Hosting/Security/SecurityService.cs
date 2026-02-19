using System.Collections.Concurrent;
using System.Linq;
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
/// Uses per-namespace Access partitions for access management:
/// - Global access: Access/ partition (e.g., Access/Roland.json for global admin)
/// - Namespace access: {namespace}/Access/ partition (e.g., ACME/Access/Alice.json)
/// - Anonymous access: Use "Public" as userId (e.g., MeshWeaver/Access/Public.json)
/// </summary>
public class SecurityService : ISecurityService
{
    private const string AccessPartitionName = "Access";

    private readonly IPersistenceService _persistence;
    private readonly AccessService _accessService;
    private readonly ILogger<SecurityService> _logger;
    private readonly IMessageHub _hub;

    // In-memory cache for performance
    private readonly ConcurrentDictionary<string, List<UserAccess>> _accessCache = new();
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

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

    private JsonSerializerOptions JsonOptions => _hub.JsonSerializerOptions;

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

        // If no user context, check "Public" user permissions
        if (string.IsNullOrEmpty(userId))
            userId = WellKnownUsers.Public;

        return GetEffectivePermissionsAsync(nodePath, userId, ct);
    }

    public async Task<Permission> GetEffectivePermissionsAsync(string nodePath, string userId, CancellationToken ct = default)
    {
        // For empty userId, use "Public"
        if (string.IsNullOrEmpty(userId))
            userId = WellKnownUsers.Public;

        var cacheKey = $"{userId}:{nodePath}";
        if (_permissionCache.TryGetValue(cacheKey, out Permission cached))
            return cached;

        // Collect effective permissions from role assignments
        var effectivePermissions = Permission.None;

        // Check Access partitions - UserAccess with hierarchical roles
        var effectiveRoles = await GetEffectiveRolesAsync(userId, nodePath, ct);
        foreach (var userRole in effectiveRoles)
        {
            var role = await GetRoleAsync(userRole.RoleId, ct);
            if (role != null)
            {
                effectivePermissions |= role.Permissions;
            }
        }

        // Add permissions from AccessContext.Roles (claim-based roles)
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

        _permissionCache.Set(cacheKey, effectivePermissions, PermissionCacheOptions);
        return effectivePermissions;
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

    #region Role Definitions (Global Access Partition)

    // Cache for custom roles
    private readonly ConcurrentDictionary<string, Role> _customRoleCache = new();
    private bool _customRolesLoaded;

    public async Task<Role?> GetRoleAsync(string roleId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(roleId))
            return null;

        // Check built-in roles first
        if (BuiltInRoles.TryGetValue(roleId, out var builtInRole))
            return builtInRole;

        // Check custom roles cache
        if (_customRoleCache.TryGetValue(roleId, out var cached))
            return cached;

        // Load all custom roles from global Access partition
        await LoadCustomRolesAsync(ct);
        return _customRoleCache.GetValueOrDefault(roleId);
    }

    public async IAsyncEnumerable<Role> GetRolesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        // Return built-in roles first
        foreach (var role in BuiltInRoles.Values)
        {
            yield return role;
        }

        // Load and return custom roles from global Access partition
        await LoadCustomRolesAsync(ct);
        foreach (var role in _customRoleCache.Values)
        {
            yield return role;
        }
    }

    public async Task SaveRoleAsync(Role role, CancellationToken ct = default)
    {
        // Don't allow overwriting built-in roles
        if (BuiltInRoles.ContainsKey(role.Id))
        {
            throw new InvalidOperationException($"Cannot modify built-in role: {role.Id}");
        }

        // Load existing objects from global Access partition
        var objects = new List<object>();
        await foreach (var obj in _persistence.GetPartitionObjectsAsync(AccessPartitionName, null).WithCancellation(ct))
        {
            // Keep all objects except existing Role with same Id
            if (obj is Role existing && existing.Id == role.Id)
                continue;
            objects.Add(obj);
        }

        // Add the new/updated role
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
            {
                _customRoleCache[role.Id] = role;
            }
        }
        _customRolesLoaded = true;
    }

    #endregion

    #region User Access Management (Per-Namespace Access Partitions)

    /// <summary>
    /// Gets the partition path for access storage.
    /// Global access uses "Access", namespace access uses "{namespace}/Access".
    /// </summary>
    private static string GetAccessPartitionPath(string? targetNamespace)
        => string.IsNullOrEmpty(targetNamespace)
            ? AccessPartitionName
            : $"{targetNamespace}/{AccessPartitionName}";

    /// <summary>
    /// Gets all UserAccess records from a specific partition (cached).
    /// </summary>
    private async Task<List<UserAccess>> GetAccessRecordsAsync(string partitionPath, CancellationToken ct)
    {
        if (_accessCache.TryGetValue(partitionPath, out var cached))
            return cached;

        await _cacheLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_accessCache.TryGetValue(partitionPath, out cached))
                return cached;

            var records = new List<UserAccess>();
            await foreach (var obj in _persistence.GetPartitionObjectsAsync(partitionPath, null).WithCancellation(ct))
            {
                if (obj is UserAccess userAccess)
                    records.Add(userAccess);
            }

            _accessCache[partitionPath] = records;
            return records;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Saves UserAccess records to a specific partition (and updates cache).
    /// </summary>
    private async Task SaveAccessRecordsAsync(string partitionPath, List<UserAccess> records, CancellationToken ct)
    {
        await _persistence.SavePartitionObjectsAsync(partitionPath, null, records.Cast<object>().ToList(), ct);
        _accessCache[partitionPath] = records;
    }

    /// <summary>
    /// Gets a user's access configuration by aggregating from relevant partitions.
    /// If targetNamespace is provided, includes roles from that namespace and its ancestors.
    /// Always includes global roles.
    /// </summary>
    public async Task<UserAccess?> GetUserAccessAsync(string userId, CancellationToken ct = default)
        => await GetUserAccessAsync(userId, null, ct);

    /// <summary>
    /// Gets a user's access configuration by aggregating from relevant partitions.
    /// If targetNamespace is provided, includes roles from that namespace and its ancestors.
    /// Always includes global roles.
    /// </summary>
    public async Task<UserAccess?> GetUserAccessAsync(string userId, string? targetNamespace, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId))
            return null;

        var allRoles = new List<UserRole>();
        string? displayName = null;

        // Check global access partition
        var globalRecords = await GetAccessRecordsAsync(AccessPartitionName, ct);
        var globalAccess = globalRecords.FirstOrDefault(u => u.UserId == userId);
        if (globalAccess != null)
        {
            allRoles.AddRange(globalAccess.Roles);
            displayName = globalAccess.DisplayName;
        }

        // If a target namespace is specified, also check that namespace and its ancestors
        if (!string.IsNullOrEmpty(targetNamespace))
        {
            var pathsToCheck = GetPathHierarchy(targetNamespace, true);
            foreach (var path in pathsToCheck)
            {
                var partitionPath = GetAccessPartitionPath(path);
                var records = await GetAccessRecordsAsync(partitionPath, ct);
                var userAccess = records.FirstOrDefault(u => u.UserId == userId);
                if (userAccess != null)
                {
                    allRoles.AddRange(userAccess.Roles);
                    displayName ??= userAccess.DisplayName;
                }
            }
        }

        if (allRoles.Count == 0)
            return null;

        return new UserAccess
        {
            UserId = userId,
            DisplayName = displayName,
            Roles = allRoles
        };
    }

    /// <summary>
    /// Saves a user's access configuration to the specified namespace partition.
    /// </summary>
    public async Task SaveUserAccessAsync(UserAccess userAccess, CancellationToken ct = default)
    {
        // For simplified model, we need the caller to specify where to save
        // This saves to the global partition - use AddUserRoleAsync for namespace-specific
        _logger.LogInformation("Saving user access for {UserId} with {RoleCount} roles to global partition",
            userAccess.UserId, userAccess.Roles.Count);

        var records = await GetAccessRecordsAsync(AccessPartitionName, ct);
        records = records.Where(u => u.UserId != userAccess.UserId).ToList();
        records.Add(userAccess);
        await SaveAccessRecordsAsync(AccessPartitionName, records, ct);
    }

    /// <summary>
    /// Saves a user's access configuration to a specific namespace partition.
    /// </summary>
    public async Task SaveUserAccessAsync(UserAccess userAccess, string targetNamespace, CancellationToken ct = default)
    {
        _logger.LogInformation("Saving user access for {UserId} with {RoleCount} roles to {Namespace}",
            userAccess.UserId, userAccess.Roles.Count, targetNamespace ?? "(global)");

        var partitionPath = GetAccessPartitionPath(targetNamespace);
        var records = await GetAccessRecordsAsync(partitionPath, ct);
        records = records.Where(u => u.UserId != userAccess.UserId).ToList();
        records.Add(userAccess);
        await SaveAccessRecordsAsync(partitionPath, records, ct);
    }

    /// <summary>
    /// Gets all user access configurations from the global partition.
    /// </summary>
    public async IAsyncEnumerable<UserAccess> GetAllUserAccessAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var records = await GetAccessRecordsAsync(AccessPartitionName, ct);
        foreach (var record in records)
        {
            yield return record;
        }
    }

    /// <summary>
    /// Gets all users who have access to a specific namespace.
    /// Checks the namespace partition, ancestor partitions, and global partition.
    /// </summary>
    public async IAsyncEnumerable<UserAccess> GetUsersWithAccessToNamespaceAsync(
        string targetNamespace,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seenUsers = new HashSet<string>();

        // Check namespace-specific partition
        var namespacePartition = GetAccessPartitionPath(targetNamespace);
        var namespaceRecords = await GetAccessRecordsAsync(namespacePartition, ct);
        foreach (var record in namespaceRecords)
        {
            seenUsers.Add(record.UserId);
            yield return record;
        }

        // Check ancestor namespaces (roles on parent namespaces inherit to children)
        var ancestors = GetPathHierarchy(targetNamespace, true).Skip(1);
        foreach (var ancestor in ancestors)
        {
            var ancestorPartition = GetAccessPartitionPath(ancestor);
            var ancestorRecords = await GetAccessRecordsAsync(ancestorPartition, ct);
            foreach (var record in ancestorRecords.Where(r => !seenUsers.Contains(r.UserId)))
            {
                seenUsers.Add(record.UserId);
                yield return record;
            }
        }

        // Check global partition (users with global roles have access everywhere)
        var globalRecords = await GetAccessRecordsAsync(AccessPartitionName, ct);
        foreach (var record in globalRecords.Where(r => !seenUsers.Contains(r.UserId)))
        {
            seenUsers.Add(record.UserId);
            yield return record;
        }
    }

    /// <summary>
    /// Adds a role to a user's access configuration for a specific namespace.
    /// </summary>
    public async Task AddUserRoleAsync(string userId, string roleId, string? targetNamespace, string? assignedBy = null, CancellationToken ct = default)
    {
        _logger.LogInformation("Adding role {RoleId} to user {UserId} on namespace {Namespace} by {AssignedBy}",
            roleId, userId, targetNamespace ?? "(global)", assignedBy ?? "(system)");

        var partitionPath = GetAccessPartitionPath(targetNamespace);
        var records = await GetAccessRecordsAsync(partitionPath, ct);

        var existingAccess = records.FirstOrDefault(u => u.UserId == userId);
        if (existingAccess != null)
        {
            // Check if role already exists as a grant (non-denied)
            if (existingAccess.Roles.Any(r => r.RoleId == roleId && !r.Denied))
                return; // Role already assigned as grant

            // Remove any denied version of this role, then add the grant
            var updatedRoles = existingAccess.Roles
                .Where(r => r.RoleId != roleId)
                .Append(new UserRole
                {
                    RoleId = roleId,
                    AssignedAt = DateTimeOffset.UtcNow,
                    AssignedBy = assignedBy
                })
                .ToList();

            var updatedAccess = existingAccess with { Roles = updatedRoles };
            records = records.Where(u => u.UserId != userId).Append(updatedAccess).ToList();
        }
        else
        {
            // Create new access record
            records.Add(new UserAccess
            {
                UserId = userId,
                Roles =
                [
                    new UserRole
                    {
                        RoleId = roleId,
                        AssignedAt = DateTimeOffset.UtcNow,
                        AssignedBy = assignedBy
                    }
                ]
            });
        }

        await SaveAccessRecordsAsync(partitionPath, records, ct);
    }

    /// <summary>
    /// Removes a role from a user's access configuration for a specific namespace.
    /// </summary>
    public async Task RemoveUserRoleAsync(string userId, string roleId, string? targetNamespace, CancellationToken ct = default)
    {
        _logger.LogInformation("Removing role {RoleId} from user {UserId} on namespace {Namespace}",
            roleId, userId, targetNamespace ?? "(global)");

        var partitionPath = GetAccessPartitionPath(targetNamespace);
        var records = await GetAccessRecordsAsync(partitionPath, ct);

        var existingAccess = records.FirstOrDefault(u => u.UserId == userId);
        if (existingAccess == null)
            return;

        var updatedRoles = existingAccess.Roles
            .Where(r => r.RoleId != roleId)
            .ToList();

        if (updatedRoles.Count == existingAccess.Roles.Count)
            return; // No role removed

        if (updatedRoles.Count == 0)
        {
            // No more roles in this namespace, remove the record
            records = records.Where(u => u.UserId != userId).ToList();
        }
        else
        {
            // Update with remaining roles
            var updatedAccess = existingAccess with { Roles = updatedRoles };
            records = records.Where(u => u.UserId != userId).Append(updatedAccess).ToList();
        }

        await SaveAccessRecordsAsync(partitionPath, records, ct);
    }

    /// <summary>
    /// Gets the effective roles for a user at a specific namespace path.
    /// Considers global roles and hierarchical inheritance from parent namespaces.
    /// Uses closest-wins semantics: for each roleId, the assignment at the deepest
    /// (most specific) path wins. If that assignment has Denied=true, the role is excluded.
    /// </summary>
    public async Task<IReadOnlyList<UserRole>> GetEffectiveRolesAsync(string userId, string targetNamespace, CancellationToken ct = default)
    {
        // Collect all assignments from deepest to shallowest
        // Order: self path first, then ancestors (deepest to shallowest), then global
        var roleAssignments = new Dictionary<string, (UserRole Role, int Depth)>();

        // Check namespace and all ancestor partitions (deepest first from GetPathHierarchy)
        var pathsToCheck = GetPathHierarchy(targetNamespace, true);
        for (var i = 0; i < pathsToCheck.Count; i++)
        {
            var path = pathsToCheck[i];
            var depth = pathsToCheck.Count - i; // deeper paths get higher depth
            var partitionPath = GetAccessPartitionPath(path);
            var records = await GetAccessRecordsAsync(partitionPath, ct);
            var userAccess = records.FirstOrDefault(u => u.UserId == userId);
            if (userAccess != null)
            {
                foreach (var role in userAccess.Roles)
                {
                    // Only keep the deepest (first encountered) assignment per roleId
                    if (!roleAssignments.ContainsKey(role.RoleId))
                    {
                        roleAssignments[role.RoleId] = (role, depth);
                    }
                }
            }
        }

        // Check global partition (depth 0 = shallowest)
        var globalRecords = await GetAccessRecordsAsync(AccessPartitionName, ct);
        var globalAccess = globalRecords.FirstOrDefault(u => u.UserId == userId);
        if (globalAccess != null)
        {
            foreach (var role in globalAccess.Roles)
            {
                if (!roleAssignments.ContainsKey(role.RoleId))
                {
                    roleAssignments[role.RoleId] = (role, 0);
                }
            }
        }

        // Return only non-denied roles
        return roleAssignments.Values
            .Where(a => !a.Role.Denied)
            .Select(a => a.Role)
            .ToList();
    }

    /// <summary>
    /// Gets raw role assignments from all levels (global, ancestors, self) for a node path.
    /// Each assignment includes its source path and whether it's local or inherited.
    /// </summary>
    public async IAsyncEnumerable<AccessAssignment> GetAccessAssignmentsAsync(
        string nodePath,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var pathsToCheck = GetPathHierarchy(nodePath, true);
        var localPath = pathsToCheck[0]; // The node itself

        // Yield assignments from the node's own partition (IsLocal = true)
        var localPartition = GetAccessPartitionPath(localPath);
        var localRecords = await GetAccessRecordsAsync(localPartition, ct);
        foreach (var record in localRecords)
        {
            foreach (var role in record.Roles)
            {
                yield return new AccessAssignment(
                    record.UserId,
                    record.DisplayName,
                    role.RoleId,
                    localPath,
                    role.Denied,
                    IsLocal: true
                );
            }
        }

        // Yield assignments from ancestor partitions (IsLocal = false)
        foreach (var ancestorPath in pathsToCheck.Skip(1))
        {
            var ancestorPartition = GetAccessPartitionPath(ancestorPath);
            var ancestorRecords = await GetAccessRecordsAsync(ancestorPartition, ct);
            foreach (var record in ancestorRecords)
            {
                foreach (var role in record.Roles)
                {
                    yield return new AccessAssignment(
                        record.UserId,
                        record.DisplayName,
                        role.RoleId,
                        ancestorPath,
                        role.Denied,
                        IsLocal: false
                    );
                }
            }
        }

        // Yield assignments from the global partition (IsLocal = false)
        var globalRecords = await GetAccessRecordsAsync(AccessPartitionName, ct);
        foreach (var record in globalRecords)
        {
            foreach (var role in record.Roles)
            {
                yield return new AccessAssignment(
                    record.UserId,
                    record.DisplayName,
                    role.RoleId,
                    "",
                    role.Denied,
                    IsLocal: false
                );
            }
        }
    }

    /// <summary>
    /// Toggles a role assignment for a user at a specific path.
    /// If denying: creates/updates a local UserRole with Denied=true.
    /// If granting: if there's a local deny record, removes it; otherwise creates a grant.
    /// </summary>
    public async Task ToggleRoleAssignmentAsync(
        string nodePath, string userId, string roleId, bool denied,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Toggling role {RoleId} for user {UserId} on {NodePath} to denied={Denied}",
            roleId, userId, nodePath, denied);

        var partitionPath = GetAccessPartitionPath(nodePath);
        var records = await GetAccessRecordsAsync(partitionPath, ct);
        var existingAccess = records.FirstOrDefault(u => u.UserId == userId);

        if (denied)
        {
            // Create or update a local deny record
            if (existingAccess != null)
            {
                // Remove any existing assignment for this role, then add deny
                var updatedRoles = existingAccess.Roles
                    .Where(r => r.RoleId != roleId)
                    .Append(new UserRole
                    {
                        RoleId = roleId,
                        Denied = true,
                        AssignedAt = DateTimeOffset.UtcNow
                    })
                    .ToList();

                var updatedAccess = existingAccess with { Roles = updatedRoles };
                records = records.Where(u => u.UserId != userId).Append(updatedAccess).ToList();
            }
            else
            {
                records.Add(new UserAccess
                {
                    UserId = userId,
                    Roles =
                    [
                        new UserRole
                        {
                            RoleId = roleId,
                            Denied = true,
                            AssignedAt = DateTimeOffset.UtcNow
                        }
                    ]
                });
            }
        }
        else
        {
            // Granting: remove local deny record if it exists
            if (existingAccess != null)
            {
                var denyRecord = existingAccess.Roles.FirstOrDefault(r => r.RoleId == roleId && r.Denied);
                if (denyRecord != null)
                {
                    // Remove the deny record (let inherited grant take effect)
                    var updatedRoles = existingAccess.Roles
                        .Where(r => !(r.RoleId == roleId && r.Denied))
                        .ToList();

                    if (updatedRoles.Count == 0)
                    {
                        records = records.Where(u => u.UserId != userId).ToList();
                    }
                    else
                    {
                        var updatedAccess = existingAccess with { Roles = updatedRoles };
                        records = records.Where(u => u.UserId != userId).Append(updatedAccess).ToList();
                    }
                }
                // If no deny record exists and no grant exists, create a grant
                else if (!existingAccess.Roles.Any(r => r.RoleId == roleId && !r.Denied))
                {
                    var updatedRoles = existingAccess.Roles
                        .Append(new UserRole
                        {
                            RoleId = roleId,
                            AssignedAt = DateTimeOffset.UtcNow
                        })
                        .ToList();

                    var updatedAccess = existingAccess with { Roles = updatedRoles };
                    records = records.Where(u => u.UserId != userId).Append(updatedAccess).ToList();
                }
            }
            else
            {
                // No existing record - create a new grant
                records.Add(new UserAccess
                {
                    UserId = userId,
                    Roles =
                    [
                        new UserRole
                        {
                            RoleId = roleId,
                            AssignedAt = DateTimeOffset.UtcNow
                        }
                    ]
                });
            }
        }

        await SaveAccessRecordsAsync(partitionPath, records, ct);
        ClearAccessCache();
    }

    /// <summary>
    /// Clears the access cache (useful for testing or after bulk updates).
    /// </summary>
    public void ClearAccessCache()
    {
        _accessCache.Clear();
        (_permissionCache as MemoryCache)?.Clear();
    }

    #endregion
}
