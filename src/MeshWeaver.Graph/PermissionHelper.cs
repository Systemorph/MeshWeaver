using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Centralized permission checking helper used by layout areas. Returns
/// <see cref="IObservable{T}"/> end-to-end — no Task, no await, no FirstAsync /
/// ToTask. When ISecurityService is not configured (RLS disabled), every
/// observable emits Permission.All / true.
/// </summary>
public static class PermissionHelper
{
    /// <summary>
    /// Effective permissions for the current user on a node path, including
    /// hub-level permission rules (e.g., WithPublicRead). Pure reactive — the
    /// returned observable emits the current value, then re-emits whenever the
    /// underlying assignments change.
    /// </summary>
    public static IObservable<Permission> GetEffectivePermissions(IMessageHub hub, string nodePath)
    {
        var securityService = hub.ServiceProvider.GetService<ISecurityService>();
        if (securityService == null)
            return Observable.Return(Permission.All);

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var userId = (accessService?.Context ?? accessService?.CircuitContext)?.ObjectId;
        var hubPermissions = hub.Configuration.Get<HubPermissionRuleSet>();

        return securityService.GetEffectivePermissions(nodePath)
            .Select(perms =>
            {
                if (hubPermissions == null || string.IsNullOrEmpty(userId))
                    return perms;
                foreach (Permission flag in Enum.GetValues<Permission>())
                {
                    if (flag != Permission.None && flag != Permission.All
                        && !perms.HasFlag(flag)
                        && hubPermissions.HasPermission(flag, null!, userId))
                    {
                        perms |= flag;
                    }
                }
                return perms;
            })
            .Catch<Permission, Exception>(ex =>
            {
                hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger("PermissionHelper")
                    ?.LogWarning(ex, "GetEffectivePermissions failed for {Path} — emitting Permission.All", nodePath);
                return Observable.Return(Permission.All);
            });
    }

    /// <summary>
    /// True when the current user has Update permission on the node.
    /// </summary>
    public static IObservable<bool> CanEdit(IMessageHub hub, string nodePath)
        => GetEffectivePermissions(hub, nodePath).Select(p => p.HasFlag(Permission.Update));

    /// <summary>
    /// True when the current user has Create permission on the parent.
    /// </summary>
    public static IObservable<bool> CanCreate(IMessageHub hub, string parentPath)
        => GetEffectivePermissions(hub, parentPath).Select(p => p.HasFlag(Permission.Create));

    /// <summary>
    /// True when the current user has Delete permission on the node.
    /// </summary>
    public static IObservable<bool> CanDelete(IMessageHub hub, string nodePath)
        => GetEffectivePermissions(hub, nodePath).Select(p => p.HasFlag(Permission.Delete));

    /// <summary>
    /// True when the current user has Comment (or Update) permission on the parent.
    /// </summary>
    public static IObservable<bool> CanComment(IMessageHub hub, string parentPath)
        => GetEffectivePermissions(hub, parentPath)
            .Select(p => p.HasFlag(Permission.Comment) || p.HasFlag(Permission.Update));

    /// <summary>
    /// Effective permissions resolving satellite nodes to their primary path.
    /// </summary>
    public static IObservable<Permission> GetEffectivePermissionsForNode(IMessageHub hub, MeshNode node)
        => GetEffectivePermissions(hub, node.GetPrimaryPath());
}
