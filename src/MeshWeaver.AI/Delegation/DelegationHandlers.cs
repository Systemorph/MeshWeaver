using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Delegation;

/// <summary>
/// Heartbeat-driven sub-thread cancellation handlers, registered on the
/// PARENT thread hub. Replaces the hard 5-min watchdog inside
/// <c>ExecuteDelegationAsync</c>: instead of a timeout that fires regardless
/// of whether the sub-thread is making progress, we observe each active
/// sub-thread's <see cref="MeshThread.LastActivityAt"/> stamp and propagate
/// cancellation only when it stops advancing.
/// </summary>
internal static class DelegationHandlers
{
    /// <summary>Default heartbeat timeout when MeshThread.HeartbeatTimeout is null.
    /// 10 s — well above any streaming chat client's normal delta cadence, low
    /// enough that hung delegations are detected promptly. Tests requiring
    /// even tighter detection override via <c>MeshThread.HeartbeatTimeout</c>.</summary>
    public static readonly TimeSpan DefaultHeartbeatTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Cold-start grace from <c>ExecutionStartedAt</c>. Sub-threads
    /// often spend the first ~5 s on agent allocation + first-token latency
    /// without writing <c>LastActivityAt</c>; 15 s covers that without
    /// allowing real hangs to pin the user too long.</summary>
    public static readonly TimeSpan ColdStartGrace = TimeSpan.FromSeconds(15);

    /// <summary>Periodic interval for the heartbeat scanner. 1 s keeps
    /// hang-detection responsive — total worst-case latency is
    /// <c>HeartbeatInterval + HeartbeatTimeout</c>.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// On the PARENT thread hub. Periodic scanner (every 1 s). Reads the
    /// hub's cached <c>AgentChatClient.ActiveDelegationPaths</c> (single
    /// source of truth for "what sub-threads are this chat's delegations
    /// currently waiting on?"), reads each sub-thread's MeshNode via the
    /// process-wide cache, and applies the heartbeat predicate. On match,
    /// posts <see cref="CancelDelegationSubThread"/> back to this hub.
    /// </summary>
    internal static IMessageDelivery HandleHeartbeatTick(
        IMessageHub hub, IMessageDelivery<HeartbeatTick> delivery)
    {
        var chat = hub.Get<AgentChatClient>();
        if (chat is null || chat.ActiveDelegationPaths.IsEmpty)
            return delivery.Processed();

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.Delegation");
        var nodeCache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var now = DateTime.UtcNow;

        foreach (var subPath in chat.ActiveDelegationPaths)
        {
            nodeCache.GetStream(subPath)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(2))
                .Subscribe(
                    node =>
                    {
                        if (node?.Content is not MeshThread t || !t.IsExecuting)
                            return;
                        var timeout = t.HeartbeatTimeout ?? DefaultHeartbeatTimeout;
                        var startedAt = t.ExecutionStartedAt ?? now;
                        if (now - startedAt <= ColdStartGrace) return;
                        var lastActivity = t.LastActivityAt ?? startedAt;
                        if (now - lastActivity <= timeout) return;

                        logger?.LogWarning(
                            "[DelegationHandlers] heartbeat stale sub={Path} sinceActivity={Since}s timeout={Timeout}s — cancelling",
                            subPath,
                            (int)(now - lastActivity).TotalSeconds, (int)timeout.TotalSeconds);
                        hub.Post(new CancelDelegationSubThread(subPath,
                            $"Heartbeat: no activity for {(int)(now - lastActivity).TotalSeconds}s"));
                    },
                    _ => { /* swallow; next tick retries */ });
        }

        return delivery.Processed();
    }

    /// <summary>
    /// On the PARENT thread hub. Writes <see cref="MeshThread.RequestedCancellationAt"/>
    /// to the named sub-thread via the process-wide
    /// <see cref="IMeshNodeStreamCache"/> — same write the GUI Stop button
    /// performs, same propagation path. The sub-thread's own cancel watcher
    /// reacts and tears down its CTS.
    /// </summary>
    internal static IMessageDelivery HandleCancelDelegationSubThread(
        IMessageHub hub, IMessageDelivery<CancelDelegationSubThread> delivery)
    {
        var req = delivery.Message;
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.Delegation");
        var nodeCache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

        logger?.LogInformation(
            "[DelegationHandlers] CancelDelegationSubThread sub={Path} reason={Reason}",
            req.SubThreadPath, req.Reason);

        nodeCache.Update(req.SubThreadPath, curr =>
                curr?.Content is MeshThread t
                    ? curr with { Content = t with { RequestedCancellationAt = DateTime.UtcNow } }
                    : curr!)
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "[DelegationHandlers] cancel write failed for {Path}", req.SubThreadPath));

        return delivery.Processed();
    }
}
