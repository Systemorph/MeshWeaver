using System.Reactive.Linq;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;


/// <summary>
/// Canonical client-side surface for permission checks. Application code
/// asks the hub for an answer; the extension dispatches through an
/// <see cref="EffectivePermissionsDelegate"/> registered in DI at startup
/// time. When <c>AddRowLevelSecurity()</c> ran, that delegate is
/// <see cref="PermissionEvaluator.GetEffectivePermissions(MeshWeaver.Messaging.IMessageHub, string, string)"/>; otherwise it's
/// the default <c>Observable.Return(Permission.All)</c>. <strong>No runtime
/// branching at the call site</strong> — same lambda for both worlds.
///
/// <para>Each extension returns an <see cref="IObservable{T}"/> end-to-end —
/// no Task, no await, no <c>FirstAsync()</c> bridge in src/. Tests bridge to
/// Task at their edge with <c>.FirstAsync().ToTask()</c>.</para>
/// </summary>
public static class HubPermissionExtensions
{
    /// <summary>
    /// Effective permissions for the current user (resolved from
    /// <see cref="AccessService.Context"/> / <see cref="AccessService.CircuitContext"/>)
    /// on <paramref name="nodePath"/>.
    /// </summary>
    public static IObservable<Permission> GetEffectivePermissions(
        this IMessageHub hub,
        string nodePath)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var userId = ResolveUserId(hub);
        return ResolveEvaluator(hub)(hub, nodePath, userId);
    }

    /// <summary>
    /// Effective permissions for the explicit <paramref name="userId"/> on
    /// <paramref name="nodePath"/>.
    /// </summary>
    public static IObservable<Permission> GetEffectivePermissions(
        this IMessageHub hub,
        string nodePath,
        string userId)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return ResolveEvaluator(hub)(hub, nodePath, userId);
    }

    /// <summary>
    /// True when the current user has <paramref name="permission"/> on
    /// <paramref name="nodePath"/>.
    /// </summary>
    public static IObservable<bool> CheckPermission(
        this IMessageHub hub,
        string nodePath,
        Permission permission)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (permission == Permission.None)
            return Observable.Return(true);
        return hub.GetEffectivePermissions(nodePath).Select(p => p.HasFlag(permission));
    }

    /// <summary>
    /// True when <paramref name="userId"/> has <paramref name="permission"/> on
    /// <paramref name="nodePath"/>.
    /// </summary>
    public static IObservable<bool> CheckPermission(
        this IMessageHub hub,
        string nodePath,
        string userId,
        Permission permission)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (permission == Permission.None)
            return Observable.Return(true);
        return hub.GetEffectivePermissions(nodePath, userId).Select(p => p.HasFlag(permission));
    }

    /// <summary>
    /// True when the current user (resolved from <see cref="AccessService.Context"/> /
    /// <see cref="AccessService.CircuitContext"/>) is a <b>global admin</b> — i.e. an
    /// admin on the <b>Admin partition</b> (<see cref="Permission.All"/> at scope
    /// <c>Admin</c>, granted by an <c>AccessAssignment</c> in <c>Admin/_Access</c>).
    /// <para>This is the ONE canonical platform-admin predicate — every "is this user a
    /// global/platform admin?" check goes through here, never an ad-hoc role-name or
    /// root-scope check. A global admin is a platform superuser (All on every path; see
    /// the short-circuit in <see cref="PermissionEvaluator"/>). Doc/Architecture/AccessControl.md.</para>
    /// </summary>
    public static IObservable<bool> IsGlobalAdmin(this IMessageHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return hub.IsGlobalAdmin(ResolveUserId(hub));
    }

    /// <summary>
    /// True when <paramref name="userId"/> is a global admin — an admin on the Admin
    /// partition (<see cref="Permission.All"/> at scope <c>Admin</c>). See
    /// <see cref="IsGlobalAdmin(IMessageHub)"/>.
    /// </summary>
    public static IObservable<bool> IsGlobalAdmin(this IMessageHub hub, string userId)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return hub.GetEffectivePermissions(PermissionEvaluator.AdminScope, userId)
            .Select(p => p.HasFlag(Permission.All));
    }

    /// <summary>
    /// Resolve a role definition (built-in or custom) by id.
    /// </summary>
    public static IObservable<Role?> GetRole(this IMessageHub hub, string roleId)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return PermissionEvaluator.GetRole(hub, roleId);
    }

    /// <summary>All available roles — built-in + custom Role MeshNodes.</summary>
    public static IObservable<Role> GetRoles(this IMessageHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return PermissionEvaluator.GetRoles(hub);
    }

    /// <summary>
    /// The current <see cref="PartitionAccessPolicy"/> at <paramref name="targetNamespace"/>,
    /// or <c>null</c> if none.
    /// </summary>
    public static IObservable<PartitionAccessPolicy?> GetPolicy(
        this IMessageHub hub, string targetNamespace)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return PermissionEvaluator.GetPolicy(hub, targetNamespace);
    }

    private static EffectivePermissionsDelegate ResolveEvaluator(IMessageHub hub) =>
        hub.Configuration.Get<EffectivePermissionsDelegate>()
        ?? MessageHubPermissionExtensions.DefaultEvaluator;

    private static string ResolveUserId(IMessageHub hub)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var context = accessService?.Context ?? accessService?.CircuitContext;
        var userId = context?.ObjectId;
        if (string.IsNullOrEmpty(userId) || context?.IsVirtual == true)
            userId = WellKnownUsers.Anonymous;
        return userId;
    }
}
