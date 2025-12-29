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

    #region Role Configuration (NodeType Level)

    /// <summary>
    /// Gets the role configuration for a NodeType.
    /// </summary>
    /// <param name="nodeType">The NodeType identifier</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The role configuration or null if not found</returns>
    Task<RoleConfiguration?> GetRoleConfigurationAsync(string nodeType, CancellationToken ct = default);

    /// <summary>
    /// Sets or updates the role configuration for a NodeType.
    /// </summary>
    /// <param name="config">The role configuration to save</param>
    /// <param name="ct">Cancellation token</param>
    Task SetRoleConfigurationAsync(RoleConfiguration config, CancellationToken ct = default);

    #endregion

    #region Node Security Configuration (Instance Level)

    /// <summary>
    /// Gets the security configuration for a specific node.
    /// </summary>
    /// <param name="nodePath">The node path</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The security configuration or null if not found</returns>
    Task<NodeSecurityConfiguration?> GetNodeSecurityConfigurationAsync(string nodePath, CancellationToken ct = default);

    /// <summary>
    /// Sets or updates the security configuration for a specific node.
    /// </summary>
    /// <param name="config">The security configuration to save</param>
    /// <param name="ct">Cancellation token</param>
    Task SetNodeSecurityConfigurationAsync(NodeSecurityConfiguration config, CancellationToken ct = default);

    #endregion

    #region Role Assignments

    /// <summary>
    /// Assigns a role to a user for a specific node.
    /// </summary>
    /// <param name="userId">The user's ObjectId</param>
    /// <param name="roleId">The role ID to assign</param>
    /// <param name="nodePath">The node path (null for global assignment)</param>
    /// <param name="assignedBy">The ObjectId of the user making the assignment</param>
    /// <param name="ct">Cancellation token</param>
    Task AssignRoleAsync(string userId, string roleId, string? nodePath, string? assignedBy = null, CancellationToken ct = default);

    /// <summary>
    /// Removes a role assignment from a user for a specific node.
    /// </summary>
    /// <param name="userId">The user's ObjectId</param>
    /// <param name="roleId">The role ID to remove</param>
    /// <param name="nodePath">The node path (null for global assignment)</param>
    /// <param name="ct">Cancellation token</param>
    Task RemoveRoleAssignmentAsync(string userId, string roleId, string? nodePath, CancellationToken ct = default);

    /// <summary>
    /// Gets all role assignments for a user.
    /// </summary>
    /// <param name="userId">The user's ObjectId</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Async enumerable of role assignments</returns>
    IAsyncEnumerable<RoleAssignment> GetUserRoleAssignmentsAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Gets all role assignments for a node.
    /// </summary>
    /// <param name="nodePath">The node path</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Async enumerable of role assignments</returns>
    IAsyncEnumerable<RoleAssignment> GetNodeRoleAssignmentsAsync(string nodePath, CancellationToken ct = default);

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
}
