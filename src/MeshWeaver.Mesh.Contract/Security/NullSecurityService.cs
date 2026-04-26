using System.Reactive.Linq;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// No-op security service that allows all operations.
/// Used as a default when AddRowLevelSecurity() is not called.
/// </summary>
public class NullSecurityService : ISecurityService
{
    private static readonly Role[] BuiltInRoles = [Role.Admin, Role.Editor, Role.Viewer, Role.Commenter, Role.PlatformAdmin];

    /// <summary>Always grants the requested <paramref name="permission"/> on <paramref name="nodePath"/> for the current user.</summary>
    public IObservable<bool> HasPermission(string nodePath, Permission permission)
        => Observable.Return(true);

    /// <summary>Always grants the requested <paramref name="permission"/> on <paramref name="nodePath"/> for <paramref name="userId"/>.</summary>
    public IObservable<bool> HasPermission(string nodePath, string userId, Permission permission)
        => Observable.Return(true);

    /// <summary>Returns <see cref="Permission.All"/> for any path under the current user.</summary>
    public IObservable<Permission> GetEffectivePermissions(string nodePath)
        => Observable.Return(Permission.All);

    /// <summary>Returns <see cref="Permission.All"/> for any path under <paramref name="userId"/>.</summary>
    public IObservable<Permission> GetEffectivePermissions(string nodePath, string userId)
        => Observable.Return(Permission.All);

    /// <summary>Returns the built-in <see cref="Role"/> matching <paramref name="roleId"/>, or <c>null</c> if no such built-in exists.</summary>
    public IObservable<Role?> GetRole(string roleId)
        => Observable.Return<Role?>(BuiltInRoles.FirstOrDefault(r => r.Id == roleId));

    /// <summary>Streams the built-in roles (Admin, Editor, Viewer, Commenter, PlatformAdmin).</summary>
    public IObservable<Role> GetRoles()
        => BuiltInRoles.ToObservable();

    /// <summary>Always returns <c>null</c> — the no-op service has no partition access policies.</summary>
    public IObservable<PartitionAccessPolicy?> GetPolicy(string targetNamespace)
        => Observable.Return<PartitionAccessPolicy?>(null);
}
