using System.Reactive.Linq;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

/// <summary>
/// Canonical client-side surface for permission checks. Mirrors the shape of
/// <c>HubActivityExtensions</c> / <c>HubThreadExtensions</c>: callers ask the
/// hub for an answer; the extension resolves <see cref="ISecurityService"/>
/// and forwards. Application code MUST go through this surface — never reach
/// into <see cref="ISecurityService"/> directly.
///
/// <para><b>Caching</b>: behind the scenes <see cref="ISecurityService"/>
/// composes against the process-wide <c>IMeshNodeStreamCache</c> for
/// AccessAssignment and PartitionAccessPolicy lookups (one shared sync
/// subscription per scope under <c>WellKnownUsers.System</c>) — every caller
/// sees the same warm cache regardless of which hub they're on. No per-hub
/// synced-query subscriptions, no per-hub <c>ImpersonateAsSystem</c> scope to
/// leak.</para>
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
    /// on <paramref name="nodePath"/>. Returns <see cref="Permission.All"/> when
    /// no <see cref="ISecurityService"/> is registered (RLS disabled).
    /// </summary>
    public static IObservable<Permission> GetEffectivePermissions(
        this IMessageHub hub,
        string nodePath)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var securityService = hub.ServiceProvider.GetService<ISecurityService>();
        return securityService is null
            ? Observable.Return(Permission.All)
            : securityService.GetEffectivePermissions(nodePath);
    }

    /// <summary>
    /// Effective permissions for the explicit <paramref name="userId"/> on
    /// <paramref name="nodePath"/>. Use when you need to evaluate a different
    /// user than the ambient context (e.g. admin tooling, server-to-server
    /// authorization checks).
    /// </summary>
    public static IObservable<Permission> GetEffectivePermissions(
        this IMessageHub hub,
        string nodePath,
        string userId)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var securityService = hub.ServiceProvider.GetService<ISecurityService>();
        return securityService is null
            ? Observable.Return(Permission.All)
            : securityService.GetEffectivePermissions(nodePath, userId);
    }

    /// <summary>
    /// True when the current user has <paramref name="permission"/> on
    /// <paramref name="nodePath"/>. Convenience over
    /// <see cref="GetEffectivePermissions(IMessageHub, string)"/> + <c>HasFlag</c>.
    /// </summary>
    public static IObservable<bool> CheckPermission(
        this IMessageHub hub,
        string nodePath,
        Permission permission)
        => hub.GetEffectivePermissions(nodePath).Select(p => p.HasFlag(permission));

    /// <summary>
    /// True when <paramref name="userId"/> has <paramref name="permission"/> on
    /// <paramref name="nodePath"/>.
    /// </summary>
    public static IObservable<bool> CheckPermission(
        this IMessageHub hub,
        string nodePath,
        string userId,
        Permission permission)
        => hub.GetEffectivePermissions(nodePath, userId).Select(p => p.HasFlag(permission));
}
