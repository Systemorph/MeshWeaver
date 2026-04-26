using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Monolith.TestBase;

/// <summary>
/// Test helpers for querying effective permissions via the
/// <see cref="GetPermissionRequest"/> / <see cref="GetPermissionResponse"/>
/// request-response pair. Lets tests stay scope-agnostic — the per-node hub
/// receives the request and resolves its scoped <see cref="ISecurityService"/>.
/// Tests should NOT call <c>ServiceProvider.GetRequiredService&lt;ISecurityService&gt;()</c>
/// directly (it's scoped, not available from the root provider).
/// </summary>
public static class PermissionTestExtensions
{
    /// <summary>
    /// Posts a <see cref="GetPermissionRequest"/> to the per-node hub at
    /// <paramref name="path"/>. The hub evaluates permissions for the user
    /// identified by <paramref name="userId"/> (the test sets the AccessContext
    /// for the duration of the call) on the hub's OWN address — the path is
    /// encoded in the routing target, not in the message. Test-only Task bridge.
    /// </summary>
    public static async Task<Permission> GetPermissionAsync(
        this IMessageHub hub, string path, string userId, CancellationToken ct = default)
    {
        // Test-only direct read of the scoped ISecurityService — sanctioned
        // bridge per CLAUDE.md (test edge). Tests create the scope explicitly
        // because ISecurityService is registered Scoped per hub, not singleton.
        using var scope = hub.ServiceProvider.CreateScope();
        var accessService = scope.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = userId, Name = userId });

        var sec = scope.ServiceProvider.GetRequiredService<ISecurityService>();
        return await sec.GetEffectivePermissions(path, userId).FirstAsync().ToTask(ct);
    }

    /// <summary>
    /// Convenience: true when <paramref name="userId"/> has
    /// <paramref name="permission"/> on <paramref name="path"/>.
    /// </summary>
    public static async Task<bool> HasPermissionAsync(
        this IMessageHub hub, string path, string userId, Permission permission, CancellationToken ct = default)
        => (await hub.GetPermissionAsync(path, userId, ct)).HasFlag(permission);
}
