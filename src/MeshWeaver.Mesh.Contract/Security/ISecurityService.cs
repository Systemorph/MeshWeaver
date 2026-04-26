using System.Reactive;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Service for evaluating permissions and managing security configurations.
/// Provides row-level security for mesh nodes — permissions are derived from
/// <c>AccessAssignment</c> MeshNodes in the node hierarchy.
///
/// <para><b>Surface contract — pure <see cref="IObservable{T}"/></b>. Per
/// <c>Doc/Architecture/AsynchronousCalls.md</c>: any method that participates
/// in mesh work returns <see cref="IObservable{T}"/>, never <c>Task</c>.
/// Callers compose with <c>.Select</c> / <c>.SelectMany</c> / <c>.Subscribe</c>;
/// the only place this surface bridges to a <see cref="System.Threading.Tasks.Task"/>
/// is the test edge (<c>.FirstAsync().ToTask(ct)</c>).</para>
/// </summary>
public interface ISecurityService
{
    #region Permission Evaluation

    /// <summary>
    /// True if the current user (from <c>AccessService.Context</c>) has
    /// <paramref name="permission"/> on <paramref name="nodePath"/>.
    /// </summary>
    IObservable<bool> HasPermission(string nodePath, Permission permission);

    /// <summary>
    /// True if <paramref name="userId"/> has <paramref name="permission"/> on <paramref name="nodePath"/>.
    /// </summary>
    IObservable<bool> HasPermission(string nodePath, string userId, Permission permission);

    /// <summary>
    /// All effective permissions for the current user on <paramref name="nodePath"/>.
    /// </summary>
    IObservable<Permission> GetEffectivePermissions(string nodePath);

    /// <summary>
    /// All effective permissions for <paramref name="userId"/> on <paramref name="nodePath"/>.
    /// </summary>
    IObservable<Permission> GetEffectivePermissions(string nodePath, string userId);

    #endregion

    #region Role Definitions

    /// <summary>The role with id <paramref name="roleId"/> (built-in or custom), or <c>null</c> if not found.</summary>
    IObservable<Role?> GetRole(string roleId);

    /// <summary>All available roles (built-in + custom).</summary>
    IObservable<Role> GetRoles();

    #endregion

    #region Partition Access Policies

    /// <summary>The current partition access policy at <paramref name="targetNamespace"/>, or <c>null</c> if none.</summary>
    IObservable<PartitionAccessPolicy?> GetPolicy(string targetNamespace);

    #endregion

    // ALL mutating operations are intentionally absent (AddUserRole / RemoveUserRole /
    // SetPolicy / RemovePolicy / SaveRole / any create / update / delete).
    //
    // Static seeds belong in MeshConfiguration via AddMeshNodes (DI-time, immutable).
    // Dynamic mutations from UI / handlers go through the workspace's standard data
    // layer:
    //   • workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, ref).Update(...)
    //     — for in-place updates of an existing AccessAssignment / Policy / Role node.
    //   • IMeshService.CreateNode(node) — for first-time creation.
    //   • IMeshService.DeleteNode(path) — for deletion.
    // SecurityService observes the resulting changes via the synced AccessAssignments
    // collection (registered by AddRowLevelSecurity); reads stay reactive end-to-end.
}
