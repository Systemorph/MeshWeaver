using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeshWeaver.Mesh;

/// <summary>
/// "Is this hub LEAVING?" — the one predicate a background sweep asks before it touches state that
/// other generations of this deployment share (issue #3129).
/// </summary>
/// <remarks>
/// <para>Two signals answer it, and a sweep needs BOTH:</para>
/// <list type="bullet">
///   <item><see cref="IMessageHub.IsShuttingDown"/> — this hub's own teardown, or an ancestor's
///     cascade. The shape #3109 gave <c>HandleInitialize</c> (no BuildupAction starts after it) and
///     <c>SubscribeHubWatcher</c> (no emission is delivered after it).</item>
///   <item><see cref="IHostApplicationLifetime.ApplicationStopping"/> — the PROCESS is leaving.
///     On a pod this fires at SIGTERM, minutes before any hub disposes: the mesh is drained by
///     <c>MeshTeardownHostedService.StoppedAsync</c>, i.e. at the very END of host shutdown, after
///     every other hosted service has stopped and after Kestrel has finished with the circuits the
///     pod still holds. Under a 30-minute termination grace period that is a 30-minute window in
///     which every hub in the process reads <c>IsShuttingDown == false</c> while the pod is
///     already being replaced.</item>
/// </list>
/// <para>🚨 <b>Why the hub signal alone is UNREACHABLE for the case that hurt.</b> During the roll
/// measured in #3129 a terminating pod logged 1424 <c>ADOPTION REFUSED</c> lines over 25 minutes,
/// every one issued from a watcher that <c>SubscribeHubWatcher</c> would have dropped had the hub
/// been shutting down. So a guard reading only <see cref="IMessageHub.IsShuttingDown"/> would have
/// changed nothing on that pod. The host lifetime is the signal that WAS live for the whole
/// window, and it is what <c>OrleansRoutingService</c> already consults for its own shutdown
/// routing decisions.</para>
/// <para>Optional by construction: a bare mesh in a unit test has no application lifetime, and
/// then the predicate degrades to the hub signal alone — exactly the behaviour every caller had
/// before this existed.</para>
/// </remarks>
public static class HubLeavingExtensions
{
    /// <summary>
    /// True when <paramref name="hub"/> is shutting down OR the hosting process has begun
    /// stopping. Cheap enough to evaluate at every node boundary of a sweep.
    /// </summary>
    public static bool IsLeaving(this IMessageHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (hub.IsShuttingDown)
            return true;
        // Resolved per call, not cached: the lifetime is a host singleton (one lookup), and a
        // cached token read across a hub's own disposal would be a use-after-dispose of its scope.
        var lifetime = hub.ServiceProvider.GetService<IHostApplicationLifetime>();
        return lifetime?.ApplicationStopping.IsCancellationRequested ?? false;
    }
}
