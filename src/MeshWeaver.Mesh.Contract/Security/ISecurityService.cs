namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Service for evaluating permissions and managing security configurations.
/// Provides row-level security for mesh nodes.
/// Permissions are derived from AccessAssignment MeshNodes in the node hierarchy.
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

    #region Access Assignment Management

    /// <summary>
    /// Convenience method that creates an AccessAssignment MeshNode.
    /// Equivalent to IMeshCatalog.CreateNodeAsync with an AccessAssignment content type.
    /// </summary>
    /// <param name="userId">The subject's ObjectId</param>
    /// <param name="roleId">The role ID to assign</param>
    /// <param name="targetNamespace">The namespace (null for global)</param>
    /// <param name="assignedBy">The ObjectId of the user making the assignment</param>
    /// <param name="ct">Cancellation token</param>
    Task AddUserRoleAsync(string userId, string roleId, string? targetNamespace, string? assignedBy = null, CancellationToken ct = default);

    /// <summary>
    /// Convenience method that deletes an AccessAssignment MeshNode.
    /// Equivalent to IMeshCatalog.DeleteNodeAsync for the matching assignment.
    /// </summary>
    /// <param name="userId">The subject's ObjectId</param>
    /// <param name="roleId">The role ID to remove</param>
    /// <param name="targetNamespace">The namespace (null for global)</param>
    /// <param name="ct">Cancellation token</param>
    Task RemoveUserRoleAsync(string userId, string roleId, string? targetNamespace, CancellationToken ct = default);

    #endregion

    #region Partition Access Policies

    /// <summary>
    /// Gets the partition access policy at the specified namespace, if any.
    /// </summary>
    /// <param name="targetNamespace">The namespace to check</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The policy or null if none is set</returns>
    Task<PartitionAccessPolicy?> GetPolicyAsync(string targetNamespace, CancellationToken ct = default);

    /// <summary>
    /// Sets or updates the partition access policy at the specified namespace.
    /// Creates/updates a MeshNode with nodeType "PartitionAccessPolicy" and id "_Policy".
    /// </summary>
    /// <param name="targetNamespace">The namespace to apply the policy to</param>
    /// <param name="policy">The policy to set</param>
    /// <param name="ct">Cancellation token</param>
    Task SetPolicyAsync(string targetNamespace, PartitionAccessPolicy policy, CancellationToken ct = default);

    /// <summary>
    /// Removes the partition access policy at the specified namespace.
    /// Deletes the "_Policy" MeshNode.
    /// </summary>
    /// <param name="targetNamespace">The namespace to remove the policy from</param>
    /// <param name="ct">Cancellation token</param>
    Task RemovePolicyAsync(string targetNamespace, CancellationToken ct = default);

    #endregion
}
