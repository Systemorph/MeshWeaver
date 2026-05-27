using System.ComponentModel;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// 🚨 <b>Framework-internal.</b> Application code MUST go through
/// <c>hub.CheckPermission(path, permission)</c> / <c>hub.GetEffectivePermissions(path)</c>
/// (see <c>MeshWeaver.Mesh.HubPermissionExtensions</c>). The extensions resolve this
/// service from the hub's <c>ServiceProvider</c> and forward; they're the public
/// surface so the cache / impersonation plumbing can evolve without rippling
/// through call sites.
///
/// <para>The abstract base lives in <c>MeshWeaver.Mesh.Contract</c> so that
/// downstream projects (<c>MeshWeaver.Graph</c>, <c>MeshWeaver.AI</c>, layout
/// areas) can resolve <c>SecurityService</c> without referencing the concrete
/// row-level-security implementation in <c>MeshWeaver.Hosting</c>. The concrete
/// derived type is registered via <c>AddRowLevelSecurity()</c>.</para>
///
/// <para><b>Surface contract — pure <see cref="IObservable{T}"/></b>. Per
/// <c>Doc/Architecture/AsynchronousCalls.md</c>: any method that participates
/// in mesh work returns <see cref="IObservable{T}"/>, never <c>Task</c>.
/// Callers compose with <c>.Select</c> / <c>.SelectMany</c> / <c>.Subscribe</c>;
/// the only place this surface bridges to a <see cref="System.Threading.Tasks.Task"/>
/// is the test edge (<c>.FirstAsync().ToTask(ct)</c>).</para>
/// </summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public abstract class SecurityService
{
    #region Permission Evaluation

    /// <summary>
    /// True if the current user (from <c>AccessService.Context</c>) has
    /// <paramref name="permission"/> on <paramref name="nodePath"/>.
    /// </summary>
    public abstract IObservable<bool> HasPermission(string nodePath, Permission permission);

    /// <summary>
    /// True if <paramref name="userId"/> has <paramref name="permission"/> on <paramref name="nodePath"/>.
    /// </summary>
    public abstract IObservable<bool> HasPermission(string nodePath, string userId, Permission permission);

    /// <summary>
    /// All effective permissions for the current user on <paramref name="nodePath"/>.
    /// </summary>
    public abstract IObservable<Permission> GetEffectivePermissions(string nodePath);

    /// <summary>
    /// All effective permissions for <paramref name="userId"/> on <paramref name="nodePath"/>.
    /// </summary>
    public abstract IObservable<Permission> GetEffectivePermissions(string nodePath, string userId);

    #endregion

    #region Role Definitions

    /// <summary>The role with id <paramref name="roleId"/> (built-in or custom), or <c>null</c> if not found.</summary>
    public abstract IObservable<Role?> GetRole(string roleId);

    /// <summary>All available roles (built-in + custom).</summary>
    public abstract IObservable<Role> GetRoles();

    #endregion

    #region Partition Access Policies

    /// <summary>The current partition access policy at <paramref name="targetNamespace"/>, or <c>null</c> if none.</summary>
    public abstract IObservable<PartitionAccessPolicy?> GetPolicy(string targetNamespace);

    #endregion
}
