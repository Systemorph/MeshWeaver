using System.Reactive.Linq;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// No-op <see cref="SecurityService"/> used when row-level security is not
/// configured (e.g. tests, dev mesh without <c>AddRowLevelSecurity()</c>).
/// Grants <see cref="Permission.All"/> for every probe and returns empty
/// role / policy snapshots.
/// </summary>
public sealed class NullSecurityService : SecurityService
{
    public override IObservable<bool> HasPermission(string nodePath, Permission permission) =>
        Observable.Return(true);

    public override IObservable<bool> HasPermission(string nodePath, string userId, Permission permission) =>
        Observable.Return(true);

    public override IObservable<Permission> GetEffectivePermissions(string nodePath) =>
        Observable.Return(Permission.All);

    public override IObservable<Permission> GetEffectivePermissions(string nodePath, string userId) =>
        Observable.Return(Permission.All);

    public override IObservable<Role?> GetRole(string roleId) =>
        Observable.Return<Role?>(null);

    public override IObservable<Role> GetRoles() =>
        Observable.Empty<Role>();

    public override IObservable<PartitionAccessPolicy?> GetPolicy(string targetNamespace) =>
        Observable.Return<PartitionAccessPolicy?>(null);
}
