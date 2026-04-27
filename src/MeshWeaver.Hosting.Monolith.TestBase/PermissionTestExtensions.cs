using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
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
    public static Task<Permission> GetPermissionAsync(
        this IMessageHub hub, string path, string userId, CancellationToken ct = default)
        => hub.GetPermissionAsync(path, userId, until: null, ct);

    /// <summary>
    /// Wait-for-predicate overload: subscribes to
    /// <see cref="ISecurityService.GetEffectivePermissions(string,string)"/>
    /// (a hot observable backed by the workspace's synced AccessAssignment
    /// query) and returns the first emission satisfying <paramref name="until"/>.
    ///
    /// <para>Use this from a test that just performed a runtime
    /// <c>CreateNode</c> / <c>DeleteNode</c> on an AccessAssignment — the
    /// initial cached snapshot may not yet reflect the change because the
    /// synced query's <c>Added</c> / <c>Removed</c> event from
    /// <see cref="IMeshQueryProvider.ObserveQuery"/> arrives asynchronously
    /// (debounced). Subscribers see every emission as the cache updates;
    /// the first one matching <paramref name="until"/> wins.</para>
    ///
    /// <para>Bounded by a 5-second wall clock so a test that incorrectly
    /// expects a permission that will never arrive fails loudly with a
    /// <see cref="TimeoutException"/> rather than hanging.</para>
    /// </summary>
    public static async Task<Permission> GetPermissionAsync(
        this IMessageHub hub, string path, string userId,
        Func<Permission, bool>? until,
        CancellationToken ct = default)
    {
        using var scope = hub.ServiceProvider.CreateScope();
        var accessService = scope.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = userId, Name = userId });

        var sec = scope.ServiceProvider.GetRequiredService<ISecurityService>();
        var stream = sec.GetEffectivePermissions(path, userId);
        if (until != null)
            stream = stream.Where(until).Timeout(TimeSpan.FromSeconds(5));
        return await stream.FirstAsync().ToTask(ct);
    }

    /// <summary>
    /// Convenience: true when <paramref name="userId"/> has
    /// <paramref name="permission"/> on <paramref name="path"/>.
    /// </summary>
    public static async Task<bool> HasPermissionAsync(
        this IMessageHub hub, string path, string userId, Permission permission, CancellationToken ct = default)
        => (await hub.GetPermissionAsync(path, userId, ct)).HasFlag(permission);
}
