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
    /// <para>Bounded by <paramref name="timeout"/> (default 30 s) so a test
    /// that incorrectly expects a permission that will never arrive fails
    /// loudly with a <see cref="TimeoutException"/> rather than hanging.</para>
    /// </summary>
    public static async Task<Permission> GetPermissionAsync(
        this IMessageHub hub, string path, string userId,
        Func<Permission, bool>? until,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        // SecurityService.GetEffectivePermissions takes the userId explicitly,
        // so we don't need to mutate AccessService.CircuitContext for the
        // probe. The previous shape stamped CircuitContext = userId on the
        // singleton AccessService and never restored it — every subsequent
        // call (e.g., the runtime DeleteNode immediately after a
        // until-polling probe) saw the probe's identity instead of the
        // DevLogin admin context the test had set, and the delete failed
        // with "Delete permission denied".
        var sec = hub.ServiceProvider.GetRequiredService<ISecurityService>();
        if (until == null)
            return await sec.GetEffectivePermissions(path, userId).FirstAsync().ToTask(ct);

        // Wait for the predicate to match. SecurityService.GetEffectivePermissions
        // is a hot observable backed by per-scope synced AccessAssignment
        // queries with Replay(1).RefCount(); long-lived subscribers see runtime
        // satellites surface as the synced query re-emits. Plain
        // .Where(until).Timeout(...).FirstAsync() is the canonical pattern —
        // no polling, no Task.Delay. Timeout(60s) gives the synced query
        // headroom on slow CI before failing the test with TimeoutException
        // rather than hanging.
        var t = timeout ?? TimeSpan.FromSeconds(60);
        return await sec.GetEffectivePermissions(path, userId)
            .Where(until)
            .Timeout(t)
            .FirstAsync()
            .ToTask(ct);
    }

    /// <summary>
    /// Convenience: true when <paramref name="userId"/> has
    /// <paramref name="permission"/> on <paramref name="path"/>.
    /// </summary>
    public static async Task<bool> HasPermissionAsync(
        this IMessageHub hub, string path, string userId, Permission permission, CancellationToken ct = default)
        => (await hub.GetPermissionAsync(path, userId, ct)).HasFlag(permission);

    /// <summary>
    /// Stream-based wait that succeeds the first time
    /// <paramref name="userId"/> has <paramref name="permission"/> on
    /// <paramref name="path"/>. Subscribes to the live
    /// <c>GetEffectivePermissions</c> stream so we react to the synced
    /// AccessAssignment query's NEXT emission — no polling, no
    /// <c>Task.Delay</c>. Throws <see cref="TimeoutException"/> if the
    /// permission never arrives within <paramref name="timeout"/>.
    /// </summary>
    public static Task WaitForPermissionAsync(
        this IMessageHub hub, string path, string userId, Permission permission,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
        => hub.GetPermissionAsync(
            path, userId,
            until: p => p.HasFlag(permission),
            ct: ct,
            timeout: timeout);
}
