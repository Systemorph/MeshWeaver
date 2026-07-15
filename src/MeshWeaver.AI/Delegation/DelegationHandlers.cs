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
    /// <summary>Periodic interval for the heartbeat scanner. 1 s keeps
    /// hang-detection responsive — total worst-case latency is
    /// <c>HeartbeatInterval + HeartbeatTimeout</c>.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    /// <summary>Fallback thresholds when no <see cref="DelegationHeartbeatOptions"/>
    /// is registered (production default). Immutable — a shared read-only constant.</summary>
    public static readonly DelegationHeartbeatOptions DefaultHeartbeatOptions = new();

    /// <summary>
    /// On the PARENT thread hub. Periodic scanner (every 1 s). Reads the
    /// hub's cached <c>AgentChatClient.ActiveDelegationPaths</c> (single
    /// source of truth for "what sub-threads are this chat's delegations
    /// currently waiting on?"), reads each sub-thread's MeshNode via the
    /// process-wide cache, and applies the heartbeat predicate. On match,
    /// posts <see cref="CancelDelegationSubThread"/> back to this hub.
    ///
    /// <para><b>Two windows, not one.</b> A sub-thread that has produced NO activity
    /// yet (<c>LastActivityAt == null</c>) is in FIRST-TOKEN LATENCY — agent
    /// allocation + model time-to-first-token + first tool round-trip — which is NOT
    /// a stalled stream. A reasoning model, a cold provider endpoint, or a slow first
    /// tool legitimately takes far longer than the inter-activity timeout to emit its
    /// first delta; judging that window by the short timeout cancelled a live-but-slow
    /// sub-agent before it ever started (the "sub-thread never started" symptom this
    /// method used to cause). Such a sub-thread is judged by
    /// <see cref="DelegationHeartbeatOptions.FirstActivityBudget"/> instead. Only once
    /// the sub-agent HAS stamped an activity does the inter-activity
    /// <see cref="DelegationHeartbeatOptions.HeartbeatTimeout"/> apply. A genuinely
    /// hung agent that never emits is still caught (after the first-activity budget),
    /// and the parent's 10-min <c>delegate_to_agent</c> timeout is the ultimate backstop.</para>
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
        var opts = hub.ServiceProvider.GetService<DelegationHeartbeatOptions>()
                   ?? DefaultHeartbeatOptions;
        var now = DateTime.UtcNow;

        foreach (var subPath in chat.ActiveDelegationPaths)
        {
            // 🚨 TYPED overload — the bare GetStream(path) emits raw JsonElement
            // Content, so `node.Content is not MeshThread` would be TRUE for every
            // sub-thread and the heartbeat scan would silently never fire.
            nodeCache.GetStream(subPath, hub.JsonSerializerOptions)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(2))
                .Subscribe(
                    node =>
                    {
                        if (node?.Content is not MeshThread t || !t.IsExecuting)
                            return;
                        var startedAt = t.ExecutionStartedAt ?? now;

                        string reason;
                        if (t.LastActivityAt is not { } lastActivity)
                        {
                            // Never active yet → still in first-token latency. Judge by the
                            // (generous) first-activity budget, NOT the inter-activity timeout.
                            var firstBudget = t.FirstActivityBudget ?? opts.FirstActivityBudget;
                            if (now - startedAt <= firstBudget) return;
                            reason = $"no first activity within {(int)firstBudget.TotalSeconds}s of start";
                        }
                        else
                        {
                            // Was active, now silent → a genuinely stalled stream. Apply the
                            // inter-activity timeout, keeping the post-start settle grace.
                            if (now - startedAt <= opts.ColdStartGrace) return;
                            var timeout = t.HeartbeatTimeout ?? opts.HeartbeatTimeout;
                            if (now - lastActivity <= timeout) return;
                            reason = $"no activity for {(int)(now - lastActivity).TotalSeconds}s";
                        }

                        logger?.LogWarning(
                            "[DelegationHandlers] heartbeat stale sub={Path} — cancelling ({Reason})",
                            subPath, reason);
                        hub.Post(new CancelDelegationSubThread(subPath, $"Heartbeat: {reason}"));
                    },
                    _ => { /* swallow; next tick retries */ });
        }

        return delivery.Processed();
    }

    /// <summary>
    /// On the PARENT thread hub. Sets <see cref="MeshThread.RequestedStatus"/>
    /// = <c>Cancelled</c> on the named sub-thread via the process-wide
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

        // 🚨 TYPED overload — the bare Update(path, fn) does NOT deserialize
        // JsonElement Content before the lambda, so `curr.Content is MeshThread`
        // would be FALSE and the cancel would silently never be written.
        nodeCache.Update(req.SubThreadPath, curr =>
                curr?.Content is MeshThread t
                    ? curr with { Content = t with { RequestedStatus = ThreadExecutionStatus.Cancelled } }
                    : curr!,
                hub.JsonSerializerOptions)
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "[DelegationHandlers] cancel write failed for {Path}", req.SubThreadPath));

        return delivery.Processed();
    }
}
