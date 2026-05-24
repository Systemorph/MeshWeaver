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
/// Message handlers that implement the race-free delegation lifecycle.
/// Two halves:
/// <list type="bullet">
///   <item><b>Thread-hub handlers</b> (parent of the delegation):
///         <c>CreateDelegationSubThread</c> — builds the sub-thread node + cells
///         in a single sequenced chain, posts <c>DelegationSubThreadCreated</c>
///         back to <c>_Exec</c> on success or a terminal-error
///         <c>SubThreadStateChanged</c> on failure;
///         <c>CancelDelegationSubThread</c> — writes <c>RequestedCancellationAt</c>
///         to the named sub-thread (same primitive the GUI Stop button uses).</item>
///   <item><b>_Exec-hub handlers</b> (per chat agent execution):
///         <c>DelegationSubThreadCreated</c> — installs THE single observation
///         subscription whose lambda only does <c>Hub.Post</c>;
///         <c>SubThreadStateChanged</c> — drains state into the per-CallId
///         <see cref="DelegationEntry"/> + writes the next frame onto the
///         channel <c>ExecuteDelegationAsync</c> is draining;
///         <c>HeartbeatTick</c> — scans the registry for stale entries
///         (no <see cref="SubThreadStateChanged"/> in HeartbeatTimeout) and
///         posts <c>CancelDelegationSubThread</c> to the parent.</item>
/// </list>
///
/// <para>All handlers are <c>internal static</c> — the framework's
/// <c>WithHandler&lt;T&gt;</c> binds them onto the relevant hubs in
/// <c>ThreadExecution.AddThreadExecution</c> (thread hub) and
/// <c>InstallExecutionHub</c> (_Exec).</para>
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

    // ──────────────────────────────────────────────────────────────────────
    // Thread hub: CreateDelegationSubThread
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// On the PARENT thread hub. Builds the sub-thread node + cells; sequences
    /// the three <c>meshService.CreateNode</c> observables via <c>.Concat()</c>
    /// so the response cell is only created after the user cell, and the
    /// sub-thread node only after both cells. On terminal success: posts
    /// <see cref="DelegationSubThreadCreated"/> to the <c>_Exec</c> hub
    /// (target derived from the parent thread address). On any failure:
    /// posts a terminal <see cref="SubThreadStateChanged"/> with the error.
    /// </summary>
    internal static IMessageDelivery HandleCreateDelegationSubThread(
        IMessageHub hub, IMessageDelivery<CreateDelegationSubThread> delivery)
    {
        var req = delivery.Message;
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.Delegation");
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        var execAddress = new Address($"{hub.Address}/_Exec");

        var (subThreadNode, userMsgId, responseMsgId) = ThreadNodeType.BuildThreadWithMessages(
            req.ParentMsgPath, req.Task,
            createdBy: delivery.AccessContext?.ObjectId,
            agentName: req.TargetAgentId);
        subThreadNode = subThreadNode with { MainNode = req.MainEntityPath };
        var subThreadPath = subThreadNode.Path!;

        logger?.LogInformation(
            "[DelegationHandlers] CreateDelegationSubThread callId={CallId} sub={Path} user={U} resp={R}",
            req.CallId, subThreadPath, userMsgId, responseMsgId);

        var userCell = new MeshNode(userMsgId, subThreadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = req.MainEntityPath,
            Content = new ThreadMessage
            {
                Role = "user", Text = req.Task, Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput,
                CreatedBy = delivery.AccessContext?.ObjectId
            }
        };
        var responseCell = new MeshNode(responseMsgId, subThreadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = req.MainEntityPath,
            Content = new ThreadMessage
            {
                Role = "assistant", Text = "", Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse, AgentName = req.TargetAgentId
            }
        };

        // Sequence the three creates. Each emits exactly once (cold IObservable
        // backed by mesh.UpdateNode/CreateNode round-trip). Concat ensures the
        // sub-thread node — and the per-node hub activation it triggers — only
        // happens once both satellite cells are persisted. Subscribers (the
        // _Exec observer installed by HandleDelegationSubThreadCreated) read
        // those cells immediately on activation; without sequencing the
        // per-node hub could see missing-satellite errors during its init.
        meshService.CreateNode(userCell)
            .Concat(meshService.CreateNode(responseCell))
            .Concat(meshService.CreateNode(subThreadNode))
            .LastAsync()
            .Subscribe(
                _ =>
                {
                    logger?.LogInformation(
                        "[DelegationHandlers] sub-thread + cells committed callId={CallId} sub={Path}",
                        req.CallId, subThreadPath);
                    hub.Post(
                        new DelegationSubThreadCreated(req.CallId, subThreadPath, responseMsgId),
                        o => o.WithTarget(execAddress));
                },
                ex =>
                {
                    logger?.LogWarning(ex,
                        "[DelegationHandlers] sub-thread create failed callId={CallId} sub={Path}",
                        req.CallId, subThreadPath);
                    hub.Post(
                        new SubThreadStateChanged(
                            req.CallId, subThreadPath,
                            AccumulatedText: null, CellStatus: ThreadMessageStatus.Error,
                            ThreadIdle: true, CellCompleted: true,
                            ErrorMessage: ex.Message),
                        o => o.WithTarget(execAddress));
                });

        return delivery.Processed();
    }

    // ──────────────────────────────────────────────────────────────────────
    // Thread hub: CancelDelegationSubThread
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// On the PARENT thread hub. Writes <c>RequestedCancellationAt</c> to the
    /// named sub-thread via the process-wide
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

    // ──────────────────────────────────────────────────────────────────────
    // _Exec hub: DelegationSubThreadCreated
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// On the <c>_Exec</c> hub. Installs THE single observation subscription
    /// for this delegation. The Subscribe lambda is schedulerless — it ONLY
    /// posts <see cref="SubThreadStateChanged"/> back into <c>_Exec</c>'s
    /// own action block, where <see cref="HandleSubThreadStateChanged"/> runs
    /// serialized with the rest of <c>_Exec</c>'s state mutations.
    /// </summary>
    internal static IMessageDelivery HandleDelegationSubThreadCreated(
        IMessageHub hub, IMessageDelivery<DelegationSubThreadCreated> delivery)
    {
        var req = delivery.Message;
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.Delegation");
        var registry = hub.ServiceProvider.GetRequiredService<DelegationRegistry>();
        var nodeCache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

        if (!registry.Active.TryGetValue(req.CallId, out var entry))
        {
            logger?.LogWarning(
                "[DelegationHandlers] DelegationSubThreadCreated for unknown callId={CallId} — late event after cleanup",
                req.CallId);
            return delivery.Processed();
        }

        var responsePath = $"{req.SubThreadPath}/{req.ResponseMsgId}";
        var hubRef = hub;

        // ONE subscription. CombineLatest fans the two streams into a single
        // emission flow; the lambda's ONLY job is Hub.Post so the state-update
        // handler runs serialized on _Exec's action block (no race between
        // emission-time and the streaming loop's reads).
        entry.Subscription = Observable.CombineLatest(
                nodeCache.GetStream(req.SubThreadPath),
                nodeCache.GetStream(responsePath),
                (threadNode, cellNode) => (threadNode, cellNode))
            .Subscribe(
                tuple =>
                {
                    var thread = tuple.threadNode?.Content as MeshThread;
                    var cell = tuple.cellNode?.Content as ThreadMessage;
                    hubRef.Post(new SubThreadStateChanged(
                        req.CallId,
                        req.SubThreadPath,
                        AccumulatedText: cell?.Text,
                        CellStatus: cell?.Status,
                        ThreadIdle: thread is { IsExecuting: false },
                        CellCompleted: cell?.CompletedAt is not null,
                        ErrorMessage: null));
                },
                ex => hubRef.Post(new SubThreadStateChanged(
                    req.CallId, req.SubThreadPath,
                    AccumulatedText: null, CellStatus: ThreadMessageStatus.Error,
                    ThreadIdle: true, CellCompleted: true,
                    ErrorMessage: ex.Message)));

        return delivery.Processed();
    }

    // ──────────────────────────────────────────────────────────────────────
    // _Exec hub: SubThreadStateChanged
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// On the <c>_Exec</c> hub. The only place that mutates per-delegation
    /// state (<see cref="DelegationEntry.AccumulatedText"/>) and the only
    /// place that writes to the channel <c>ExecuteDelegationAsync</c> is
    /// draining. Single-threaded by virtue of running on <c>_Exec</c>'s
    /// action block — no locks, no <c>terminalTcs</c> race.
    /// </summary>
    internal static IMessageDelivery HandleSubThreadStateChanged(
        IMessageHub hub, IMessageDelivery<SubThreadStateChanged> delivery)
    {
        var msg = delivery.Message;
        var registry = hub.ServiceProvider.GetRequiredService<DelegationRegistry>();

        if (!registry.Active.TryGetValue(msg.CallId, out var entry))
            return delivery.Processed();

        // Text delta — yield the new portion to the channel.
        if (msg.AccumulatedText is { Length: > 0 } text
            && text.Length > entry.AccumulatedText.Length)
        {
            var delta = text[entry.AccumulatedText.Length..];
            entry.AccumulatedText = text;
            entry.Writer.TryWrite(new DelegationFrame(
                Delta: delta, Terminal: false, FinalStatus: null, ErrorMessage: null));
        }

        // Terminal — thread idle (after having executed) OR cell completed OR error.
        if (msg.ErrorMessage is not null || msg.CellCompleted || msg.ThreadIdle)
        {
            entry.Writer.TryWrite(new DelegationFrame(
                Delta: null,
                Terminal: true,
                FinalStatus: msg.CellStatus,
                ErrorMessage: msg.ErrorMessage));
            entry.Writer.TryComplete();
            entry.Subscription?.Dispose();
            registry.Active.TryRemove(msg.CallId, out _);
        }

        return delivery.Processed();
    }

    // ──────────────────────────────────────────────────────────────────────
    // _Exec hub: HeartbeatTick
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// On the <c>_Exec</c> hub. Periodic scanner (every 5 s). For each active
    /// delegation, reads the sub-thread node via
    /// <c>nodeCache.GetStream(...).Take(1)</c> (already-cached on hot path —
    /// no SubscribeRequest round-trip) and applies the heartbeat predicate:
    /// <c>IsExecuting=true AND (now - LastActivityAt) &gt; HeartbeatTimeout
    /// AND (now - ExecutionStartedAt) &gt; ColdStartGrace</c>. On match,
    /// posts <see cref="CancelDelegationSubThread"/> to the parent thread
    /// hub.
    /// </summary>
    internal static IMessageDelivery HandleHeartbeatTick(
        IMessageHub hub, IMessageDelivery<HeartbeatTick> delivery)
    {
        var registry = hub.ServiceProvider.GetRequiredService<DelegationRegistry>();
        if (registry.Active.IsEmpty) return delivery.Processed();

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.Delegation");
        var nodeCache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var parentAddress = hub.Configuration.ParentHub?.Address
            ?? throw new InvalidOperationException("_Exec hub must have a parent thread hub");
        var now = DateTime.UtcNow;

        foreach (var (callId, entry) in registry.Active)
        {
            // One-shot read from the cache. Stream is already hydrated by the
            // observation subscription installed in HandleDelegationSubThreadCreated;
            // .Take(1).Timeout(2s) bounds the wait so a sick cache doesn't pin
            // the heartbeat handler. Errors → skip this tick; the next tick retries.
            nodeCache.GetStream(entry.SubThreadPath)
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
                            "[DelegationHandlers] heartbeat stale callId={CallId} sub={Path} sinceActivity={Since}s timeout={Timeout}s — cancelling",
                            callId, entry.SubThreadPath,
                            (int)(now - lastActivity).TotalSeconds, (int)timeout.TotalSeconds);
                        hub.Post(
                            new CancelDelegationSubThread(entry.SubThreadPath,
                                $"Heartbeat: no activity for {(int)(now - lastActivity).TotalSeconds}s"),
                            o => o.WithTarget(parentAddress));
                    },
                    _ => { /* swallow; next tick retries */ });
        }

        return delivery.Processed();
    }
}
