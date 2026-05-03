using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Helpers for the Activity Control Plane — the canonical pattern where the
/// owning hub watches its OWN <see cref="MeshNodeReference"/> stream for
/// <see cref="ActivityLog.RequestedStatus"/> patches and translates them into
/// internal state-machine transitions (cancel, restart, etc.). See
/// <c>Doc/Architecture/ActivityControlPlane.md</c>.
///
/// <para>
/// The Status / RequestedStatus pair decouples request from current state:
/// <c>Status</c> is "what's actually happening", <c>RequestedStatus</c> is
/// "what the user wants to happen". A consumer (UI button, automated control,
/// orchestrating script) patches <c>RequestedStatus</c> via
/// <see cref="MeshNodeStreamExtensions.UpdateMeshNode"/>; the hub picks up
/// the patch and reacts.
/// </para>
/// </summary>
public static class ActivityControlPlaneExtensions
{
    /// <summary>
    /// Subscribe to <paramref name="hub"/>'s own MeshNode stream, project
    /// <see cref="ActivityLog.RequestedStatus"/>, and invoke
    /// <paramref name="onRequestedStatus"/> whenever it changes (including the
    /// initial emission when the hub first observes its own activity content).
    ///
    /// <para>
    /// Returns an <see cref="IDisposable"/> the caller is expected to register
    /// with the hub's lifetime (typically via
    /// <c>hub.RegisterForDisposal(...)</c> from a
    /// <c>WithInitialization</c> callback). When the hub is disposed the
    /// subscription tears down with it.
    /// </para>
    ///
    /// <para>
    /// The handler runs on whatever scheduler the upstream stream emits on —
    /// usually the hub's own action block. Treat it as hub-reachable code:
    /// no <c>await</c>, compose via <c>IObservable</c> chains for follow-up
    /// work. See <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </para>
    /// </summary>
    /// <param name="hub">The owning hub — typically an Activity hub or a
    /// NodeType hub that runs operations on its own content.</param>
    /// <param name="onRequestedStatus">Callback invoked with the latest
    /// <see cref="ActivityStatus"/> request. <c>null</c> means there's no
    /// pending request (the activity content has no <c>RequestedStatus</c>
    /// or the field has been cleared after a transition).</param>
    /// <param name="logger">Optional logger; the subscription's <c>OnError</c>
    /// is forwarded here so a faulted control-plane subscription doesn't
    /// silently disappear.</param>
    public static IDisposable WatchControlPlane(
        this IMessageHub hub,
        Action<ActivityStatus?> onRequestedStatus,
        ILogger? logger = null)
    {
        if (hub is null) throw new ArgumentNullException(nameof(hub));
        if (onRequestedStatus is null) throw new ArgumentNullException(nameof(onRequestedStatus));

        logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.ActivityControlPlane");

        var workspace = hub.GetWorkspace();
        return workspace.GetMeshNodeStream()
            .Select(node => (node?.Content as ActivityLog)?.RequestedStatus)
            .DistinctUntilChanged()
            .Subscribe(
                onRequestedStatus,
                ex => logger?.LogError(ex,
                    "ActivityControlPlane subscription faulted on {Address}",
                    hub.Address));
    }
}
