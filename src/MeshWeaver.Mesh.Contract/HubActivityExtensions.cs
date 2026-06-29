using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Canonical client-side surface for Activity operations. Every <see cref="ActivityLog"/>
/// state-transition request — cancel, restart, mark-done — goes through these
/// <see cref="IMessageHub"/> extensions. The extensions patch the activity
/// node's <see cref="ActivityLog.RequestedStatus"/> via
/// <c>hub.GetWorkspace().GetMeshNodeStream(activityPath).Update(...)</c>; the
/// owning activity hub's <see cref="ActivityControlPlaneExtensions.WatchControlPlane"/>
/// subscription reacts to the patch and runs the state-machine transition
/// (cancel the CTS, restart the round, etc.).
///
/// <para><b>Tests, GUI, and plugins all call these methods.</b> No more
/// hand-rolled five-line <c>stream.Update(curr =&gt; curr.Content is ActivityLog l ?
/// curr with { Content = l with { RequestedStatus = … } } : curr)</c> sprawl —
/// callers say <c>hub.CancelActivity(path)</c> and the extension does the
/// pattern-match, the no-op guard, the logging, and the Subscribe.</para>
///
/// <para>All methods are <c>void</c> / fire-and-forget. Callers observe
/// confirmation by subscribing to the activity node's remote stream — the same
/// stream the running-activities UI already binds to.</para>
/// </summary>
public static class HubActivityExtensions
{
    // ═════════════════════════════════════════════════════════════════════
    // Cancel
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Patches <see cref="ActivityLog.RequestedStatus"/> = <see cref="ActivityStatus.Cancelled"/>
    /// on <paramref name="activityPath"/>. The activity hub's
    /// <see cref="ActivityControlPlaneExtensions.WatchControlPlane"/> handler
    /// observes the flip and runs the internal cancel (trips the stored CTS,
    /// transitions <c>Status</c> from <c>Running</c> to <c>Cancelled</c>).
    /// </summary>
    public static void CancelActivity(
        this IMessageHub hub,
        string activityPath,
        Action<string>? onError = null)
        => hub.RequestActivityStatus(activityPath, ActivityStatus.Cancelled, onError);

    // ═════════════════════════════════════════════════════════════════════
    // Generic status request (the underlying primitive)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Patches <see cref="ActivityLog.RequestedStatus"/> on the activity node
    /// at <paramref name="activityPath"/>. Use this for any state-transition
    /// request the activity hub's <see cref="ActivityControlPlaneExtensions.WatchControlPlane"/>
    /// handler is set up to honour (Cancelled / Running / etc.). No-op if the
    /// node's content isn't an <see cref="ActivityLog"/> or the request is
    /// already in-flight (same value already pending).
    /// </summary>
    public static void RequestActivityStatus(
        this IMessageHub hub,
        string activityPath,
        ActivityStatus status,
        Action<string>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(activityPath))
        {
            onError?.Invoke("RequestActivityStatus requires activityPath.");
            return;
        }

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.HubActivityExtensions");

        hub.GetWorkspace().GetMeshNodeStream(activityPath).Update(node =>
        {
            if (node.Content is not ActivityLog log) return node;
            if (log.RequestedStatus == status) return node; // no-op
            return node with { Content = log with { RequestedStatus = status } };
        }).Subscribe(
            _ => { },
            ex =>
            {
                logger?.LogWarning(ex,
                    "RequestActivityStatus({Status}): patch failed for {ActivityPath}",
                    status, activityPath);
                onError?.Invoke($"RequestActivityStatus failed: {ex.Message}");
            });
    }
}
