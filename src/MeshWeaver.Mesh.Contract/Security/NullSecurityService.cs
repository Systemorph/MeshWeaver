using System.Runtime.CompilerServices;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// No-op security service that allows all operations.
/// Used as a default when AddRowLevelSecurity() is not called.
/// </summary>
public class NullSecurityService : ISecurityService
{
    /// <inheritdoc />
    public Task<bool> HasPermissionAsync(string nodePath, Permission permission, CancellationToken ct = default)
        => Task.FromResult(true);

    /// <inheritdoc />
    public Task<bool> HasPermissionAsync(string nodePath, string userId, Permission permission, CancellationToken ct = default)
        => Task.FromResult(true);

    /// <inheritdoc />
    public Task<Permission> GetEffectivePermissionsAsync(string nodePath, CancellationToken ct = default)
        => Task.FromResult(Permission.All);

    /// <inheritdoc />
    public Task<Permission> GetEffectivePermissionsAsync(string nodePath, string userId, CancellationToken ct = default)
        => Task.FromResult(Permission.All);

    private static readonly Role[] BuiltInRoles = [Role.Admin, Role.Editor, Role.Viewer, Role.Commenter, Role.PlatformAdmin];

    /// <inheritdoc />
    public Task<Role?> GetRoleAsync(string roleId, CancellationToken ct = default)
        => Task.FromResult(BuiltInRoles.FirstOrDefault(r => r.Id == roleId));

    /// <inheritdoc />
    public async IAsyncEnumerable<Role> GetRolesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var role in BuiltInRoles)
            yield return role;
    }

    /// <inheritdoc />
    public Task SaveRoleAsync(Role role, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task AddUserRoleAsync(string userId, string roleId, string? targetNamespace, string? assignedBy = null, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task RemoveUserRoleAsync(string userId, string roleId, string? targetNamespace, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<PartitionAccessPolicy?> GetPolicyAsync(string targetNamespace, CancellationToken ct = default)
        => Task.FromResult<PartitionAccessPolicy?>(null);

    /// <inheritdoc />
    public Task SetPolicyAsync(string targetNamespace, PartitionAccessPolicy policy, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task RemovePolicyAsync(string targetNamespace, CancellationToken ct = default)
        => Task.CompletedTask;
}
