namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Service for evaluating permissions and managing security configurations.
/// Provides row-level security for mesh nodes.
/// </summary>
public interface ISecurityService
{
    #region Permission Evaluation

    /// <summary>
    /// Checks if the current user has the specified permission on a node.
    /// Uses the AccessContext from AccessService.
    /// </summary>
    /// <param name="nodePath">The node path to check</param>
    /// <param name="permission">The required permission</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the user has the permission</returns>
    Task<bool> HasPermissionAsync(string nodePath, Permission permission, CancellationToken ct = default);

    /// <summary>
    /// Checks if a specific user has the specified permission on a node.
    /// </summary>
    /// <param name="nodePath">The node path to check</param>
    /// <param name="userId">The user's ObjectId</param>
    /// <param name="permission">The required permission</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the user has the permission</returns>
    Task<bool> HasPermissionAsync(string nodePath, string userId, Permission permission, CancellationToken ct = default);

    /// <summary>
    /// Gets all effective permissions for the current user on a node.
    /// Uses the AccessContext from AccessService.
    /// </summary>
    /// <param name="nodePath">The node path to check</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Combined permissions from all applicable role assignments</returns>
    Task<Permission> GetEffectivePermissionsAsync(string nodePath, CancellationToken ct = default);

    /// <summary>
    /// Gets all effective permissions for a specific user on a node.
    /// </summary>
    /// <param name="nodePath">The node path to check</param>
    /// <param name="userId">The user's ObjectId</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Combined permissions from all applicable role assignments</returns>
    Task<Permission> GetEffectivePermissionsAsync(string nodePath, string userId, CancellationToken ct = default);

    #endregion

    #region Role Definitions

    /// <summary>
    /// Gets a role by ID (includes built-in and custom roles).
    /// </summary>
    /// <param name="roleId">The role ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The role or null if not found</returns>
    Task<Role?> GetRoleAsync(string roleId, CancellationToken ct = default);

    /// <summary>
    /// Gets all available roles (built-in and custom).
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Async enumerable of roles</returns>
    IAsyncEnumerable<Role> GetRolesAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates or updates a custom role.
    /// </summary>
    /// <param name="role">The role to save</param>
    /// <param name="ct">Cancellation token</param>
    Task SaveRoleAsync(Role role, CancellationToken ct = default);

    #endregion

    #region User Access Management (Per-Namespace Access Partitions)

    /// <summary>
    /// Gets a user's global access configuration.
    /// To include namespace-specific roles, use the overload with targetNamespace parameter.
    /// </summary>
    /// <param name="userId">The user's ObjectId</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The user's access configuration or null if not found</returns>
    Task<UserAccess?> GetUserAccessAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Gets a user's access configuration for a specific namespace.
    /// Includes global roles plus roles from the target namespace and its ancestors.
    /// </summary>
    /// <param name="userId">The user's ObjectId</param>
    /// <param name="targetNamespace">The namespace to check (null for global only)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The user's access configuration or null if not found</returns>
    Task<UserAccess?> GetUserAccessAsync(string userId, string? targetNamespace, CancellationToken ct = default);

    /// <summary>
    /// Saves a user's access configuration to the Access partition.
    /// </summary>
    /// <param name="userAccess">The user access configuration to save</param>
    /// <param name="ct">Cancellation token</param>
    Task SaveUserAccessAsync(UserAccess userAccess, CancellationToken ct = default);

    /// <summary>
    /// Gets all user access configurations.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Async enumerable of user access configurations</returns>
    IAsyncEnumerable<UserAccess> GetAllUserAccessAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all users who have access to a specific namespace.
    /// </summary>
    /// <param name="targetNamespace">The namespace to check</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Async enumerable of user access configurations</returns>
    IAsyncEnumerable<UserAccess> GetUsersWithAccessToNamespaceAsync(string targetNamespace, CancellationToken ct = default);

    /// <summary>
    /// Adds a role to a user's access configuration.
    /// </summary>
    /// <param name="userId">The user's ObjectId</param>
    /// <param name="roleId">The role ID to add</param>
    /// <param name="targetNamespace">The namespace (null for global)</param>
    /// <param name="assignedBy">The ObjectId of the user making the assignment</param>
    /// <param name="ct">Cancellation token</param>
    Task AddUserRoleAsync(string userId, string roleId, string? targetNamespace, string? assignedBy = null, CancellationToken ct = default);

    /// <summary>
    /// Removes a role from a user's access configuration.
    /// </summary>
    /// <param name="userId">The user's ObjectId</param>
    /// <param name="roleId">The role ID to remove</param>
    /// <param name="targetNamespace">The namespace (null for global)</param>
    /// <param name="ct">Cancellation token</param>
    Task RemoveUserRoleAsync(string userId, string roleId, string? targetNamespace, CancellationToken ct = default);

    #endregion
}
