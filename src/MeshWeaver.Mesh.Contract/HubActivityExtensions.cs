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
    // Durable action references
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Records a durable action on the activity at <paramref name="activityPath"/> — the caption and
    /// local target a reader can still open after the execution hub has unloaded
    /// (<see cref="ActivityLog.Actions"/>). Written through the canonical activity write
    /// (<see cref="ActivityLogAppender.Append"/>), so it shares one writer discipline with the
    /// transcript and cannot clobber a concurrent status write.
    ///
    /// <para>Idempotent on <see cref="ActivityActionReference.Id"/>: recording the same action
    /// again replaces the entry. Cold — the write runs on Subscribe and emits the updated activity
    /// node once.</para>
    ///
    /// <para>🚨 A reference this build could not offer is REFUSED here, at the write, with an
    /// <see cref="ArgumentException"/> naming the verdict: an absolute or otherwise non-local
    /// target, an unknown kind or a foreign contract version is never persisted for a later reader
    /// to trip over.</para>
    /// </summary>
    /// <param name="hub">The hub performing the write, under the caller's own access context.</param>
    /// <param name="activityPath">Path of the activity node.</param>
    /// <param name="action">The action to record.</param>
    /// <returns>A cold observable emitting the updated activity node.</returns>
    /// <exception cref="ArgumentException">The reference is not structurally available.</exception>
    public static IObservable<MeshNode> RecordActivityAction(
        this IMessageHub hub,
        string activityPath,
        ActivityActionReference action)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentException.ThrowIfNullOrEmpty(activityPath);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(action.Id, nameof(action));
        var availability = action.Availability();
        if (availability != ActivityActionAvailability.Available)
            throw new ArgumentException(
                $"Activity action '{action.Id}' cannot be recorded: {availability}.", nameof(action));

        return ActivityLogAppender.Append(hub, activityPath, [], log => log.WithAction(action));
    }

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
