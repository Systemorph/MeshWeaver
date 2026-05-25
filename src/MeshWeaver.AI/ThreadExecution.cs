using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.Json;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI;

/// <summary>
/// Handlers for thread message execution: submit, stream, cancel.
/// Registered on the Thread hub via ThreadNodeType.
/// </summary>
public static class ThreadExecution
{
    // Cell-stream patches are not free — at this cap a 30s response generates
    // ~300 patches instead of token-rate. 100ms is below the perceptual threshold
    // for "live typing" while keeping patch volume bounded.
    private static readonly TimeSpan StreamingSampleInterval = TimeSpan.FromMilliseconds(100);

    private sealed record StreamingSnapshot(
        string Text,
        ImmutableList<ToolCallEntry> ToolCalls,
        ImmutableList<NodeChangeEntry> NodeChanges);

    /// <summary>
    /// Single canonical write path for ThreadMessage cells: opens a short-lived
    /// remote synchronization stream, applies <paramref name="mutate"/> to the cell's
    /// content, and disposes. Constructs a placeholder MeshNode when the sync
    /// handshake hasn't delivered the initial state — the patch routes via StreamId
    /// regardless of local cache freshness, so the cell hub applies it correctly.
    ///
    /// Use this for one-off cell updates from outside the streaming loop
    /// (recovery, "Allocating agent…" placeholders). Writes via
    /// <see cref="IMeshNodeStreamCache.Update"/> — the same shared handle the
    /// GUI subscribers read from, so the patch is observed in order.
    /// </summary>
    internal static void UpdateResponseCell(
        IMeshNodeStreamCache cache,
        string responsePath,
        string threadPath,
        string responseMsgId,
        string mainEntity,
        Func<ThreadMessage, ThreadMessage> mutate,
        ILogger? logger)
    {
        cache.Update(responsePath, node =>
        {
            var current = node?.Content as ThreadMessage ?? new ThreadMessage
            {
                Role = "assistant",
                Text = "",
                Type = ThreadMessageType.AgentResponse,
                Status = ThreadMessageStatus.Streaming
            };
            var updated = mutate(current);
            if (updated.Status == ThreadMessageStatus.Streaming
                && updated.Text.Length < current.Text.Length)
                updated = updated with { Text = current.Text };
            return node != null
                ? node with { Content = updated }
                : new MeshNode(responseMsgId, threadPath)
                {
                    NodeType = ThreadMessageNodeType.NodeType,
                    MainNode = mainEntity,
                    Content = updated
                };
        }).Subscribe(
            _ => { },
            ex => logger?.LogWarning(ex,
                "[UpdateResponseCell] cache.Update failed for {Path}", responsePath));
    }

    /// <summary>
    /// Registers thread execution handlers on a hub configuration.
    /// Includes a startup recovery check for stale executing cells from crashed sessions.
    /// </summary>

    public static MessageHubConfiguration AddThreadExecution(this MessageHubConfiguration configuration)
        => configuration
            // SubmitMessageRequest was deleted 2026-05-25 — public submissions go
            // through ThreadSubmission.Submit → ThreadInput.AppendUserInput →
            // stream.Update writes PendingUserMessages on the thread node. The
            // submission watcher (InstallSubmissionWatcher) reacts to that, the
            // _Exec hosted hub runs the round, ExecuteMessageAsync is called
            // directly as a method (no wire message). See CLAUDE.md →
            // "GetMeshNodeStream().Update() is the ONLY mutation API".
            // Two-stage round-start: the THREAD hub atomically claims the
            // round (Status: Idle → StartingExecution) because the thread node
            // is its own; the _Exec hosted hub (see InstallExecutionHub) takes
            // over for the drain + cell creation + Status → Executing + stream.
            .WithHandler<StartExecutionTrigger>(ThreadSubmission.HandleStartExecution)
            // Internal cross-hub mutation triggers — when ThreadSubmission.Apply*
            // is invoked from a non-owner hub, it posts these so the work lands
            // in the per-thread hub's OWN context (UpdateRemote staleness
            // currently double-writes lists like MeshThread.Messages).
            .WithHandler<ResubmitTrigger>(ThreadSubmission.HandleResubmitTrigger)
            .WithHandler<DeleteFromMessageTrigger>(ThreadSubmission.HandleDeleteFromMessageTrigger)
            .WithHandler<RecordSubmissionFailureTrigger>(ThreadSubmission.HandleRecordSubmissionFailureTrigger)
            // Delegation heartbeat: scans this thread's chat.ActiveDelegationPaths
            // every HeartbeatInterval. Stale sub-threads get a CancelDelegationSubThread
            // (heartbeat-driven). Same handler also processes user-Stop-button
            // propagation when the parent thread's cancel watcher fans out.
            .WithHandler<MeshWeaver.AI.Delegation.HeartbeatTick>(
                MeshWeaver.AI.Delegation.DelegationHandlers.HandleHeartbeatTick)
            .WithHandler<MeshWeaver.AI.Delegation.CancelDelegationSubThread>(
                MeshWeaver.AI.Delegation.DelegationHandlers.HandleCancelDelegationSubThread)
            // Delegation Subscribe-callback continuations posted onto the parent
            // thread hub's action block so they run serialized with other hub work
            // (not on the mesh-service reply thread or workspace synced-query thread).
            .WithHandler<DelegationDispatchedTrigger>(HandleDelegationDispatched)
            .WithHandler<DelegationTerminalTrigger>(HandleDelegationTerminal)
            .WithInitialization(SetThreadHubIdentity)
            .WithInitialization(RecoverStaleExecutingThread)
            .WithInitialization(WatchForExecution)
            .WithInitialization(InstallCancellationWatcher)
            .WithInitialization(InstallExecutionHub)
            .WithInitialization(InstallSubmissionWatcher)
            .WithInitialization(InstallHeartbeatTicker);

    /// <summary>
    /// Eagerly creates the <c>_Exec</c> hosted hub at thread hub init time.
    /// Owns the <see cref="StartExecutionTrigger"/> handler (round-start: drain
    /// pending into Messages, materialise user cells, allocate response cell,
    /// then call <c>ExecuteMessageAsync</c> directly for the streaming pass).
    ///
    /// <para>Eager creation matters: the submission watcher targets
    /// <c>{threadAddress}/_Exec</c>, and the framework's hosted-hub routing
    /// uses <see cref="HostedHubCreation.Never"/> when forwarding — so the
    /// hub must already exist before the first watcher tick.</para>
    /// </summary>
    private static void InstallExecutionHub(IMessageHub threadHub)
    {
        threadHub.GetHostedHub(
            new Address($"{threadHub.Address}/_Exec"),
            config => config
                .WithHandler<StartExecutionTrigger>(HandleStartExecutionOnExec),
            HostedHubCreation.Always);
    }

    /// <summary>
    /// Installs the periodic <see cref="MeshWeaver.AI.Delegation.HeartbeatTick"/>
    /// emitter on the PARENT THREAD hub. Every <c>HeartbeatInterval</c> posts a
    /// tick to self; <see cref="MeshWeaver.AI.Delegation.DelegationHandlers.HandleHeartbeatTick"/>
    /// walks <c>chat.ActiveDelegationPaths</c> (cached on this hub via
    /// <c>parentHub.Set&lt;AgentChatClient&gt;</c>), reads each sub-thread's
    /// MeshNode via the process-wide cache, and posts
    /// <see cref="MeshWeaver.AI.Delegation.CancelDelegationSubThread"/> for
    /// any sub-thread whose <see cref="MeshThread.LastActivityAt"/> is older
    /// than its <see cref="MeshThread.HeartbeatTimeout"/>. Replaces the hard
    /// 5-min watchdog inside <c>ExecuteDelegationAsync</c>.
    ///
    /// <para>The tick handler short-circuits when <c>ActiveDelegationPaths</c>
    /// is empty — negligible cost when no delegations are in flight.</para>
    /// </summary>
    private static void InstallHeartbeatTicker(IMessageHub threadHub)
    {
        var sub = System.Reactive.Linq.Observable
            .Interval(MeshWeaver.AI.Delegation.DelegationHandlers.HeartbeatInterval)
            .Subscribe(_ => threadHub.Post(new MeshWeaver.AI.Delegation.HeartbeatTick()));
        threadHub.RegisterForDisposal(_ => sub.Dispose());
    }

    /// <summary>
    /// Installs the continuous server-side watcher that ingests queued user messages
    /// into new rounds and dispatches agent execution. See <see cref="ThreadSubmission"/>.
    /// </summary>
    private static void InstallSubmissionWatcher(IMessageHub hub)
    {
        var sub = ThreadSubmission.InstallServerWatcher(hub);
        // Dispose with the hub lifetime.
        hub.RegisterForDisposal(sub);
    }


    /// <summary>
    /// Sets the thread hub's access context to the thread creator's identity.
    /// Without this, the hub's default identity is its own address path,
    /// causing "Access denied" when reading child message nodes.
    ///
    /// Resolution order:
    ///   1. <see cref="MeshThread.CreatedBy"/> (set by callers that explicitly stamp it)
    ///   2. <see cref="MeshNode.CreatedBy"/> (filled by the CreateNodeRequest handler
    ///      from the requester's AccessContext) — covers the common case where the
    ///      caller didn't pass <c>createdBy</c> to <c>BuildThreadNode</c> and the
    ///      thread content's <c>CreatedBy</c> is null.
    /// </summary>
    private static void SetThreadHubIdentity(IMessageHub hub)
    {
        // One-shot read of the OWN thread node via GetDataRequest (posted to self) —
        // true request/response, no SubscribeRequest+immediate-unsubscribe.
        hub.GetMeshNode(hub.Address.ToString()).Subscribe(node =>
        {
            if (node is null) return;
            var createdBy = (node.Content as MeshThread)?.CreatedBy;
            if (string.IsNullOrEmpty(createdBy))
                createdBy = node.CreatedBy;
            if (string.IsNullOrEmpty(createdBy))
                return;
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            accessService?.SetContext(new AccessContext { ObjectId = createdBy, Name = createdBy });
        });
    }

    /// <summary>
    /// On hub startup, check if this Thread was left in IsExecuting=true state (crashed/restarted).
    /// If stale: mark the active response message as "*Cancelled*", clear execution state,
    /// and mark all ActiveProgress entries as completed. Fully non-blocking — no await.
    /// Each child thread's own hub recovery handles its own cancellation recursively.
    /// </summary>
    private static void RecoverStaleExecutingThread(IMessageHub hub)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var workspace = hub.GetWorkspace();
        var threadPath = hub.Address.Path;

        // Read the thread node via one-shot GetDataRequest (posted to self) — true
        // request/response, no workspace-collection subscription that immediately unsubscribes.
        hub.GetMeshNode(threadPath).Subscribe(threadNode =>
        {
            if (threadNode?.Content is not Thread { IsExecuting: true } thread)
                return;

            // Don't recover threads that are still actively eligible for
            // auto-execute by WatchForExecution. The two init handlers race
            // on hub startup; recovery's job is to clean up state left over
            // from an interrupted Task.Run on a previous activation, NOT to
            // clobber a thread that's about to (re)start. WatchForExecution's
            // entry condition is {IsExecuting=true, ActiveMessageId set,
            // PendingUserMessage set}, so we use the same shape as the
            // "owned by auto-execute" guard. Recovery still runs for the
            // stale case (Task.Run died → no PendingUserMessage was ever set
            // for this round, or the round already consumed it).
            //
            // The previous heuristic (started < 2 minutes ago → skip) is
            // dropped because the "no time limits" rework
            // (commit 6dc436bf5) explicitly said an in-flight execution can
            // legitimately exceed any time window, and a long-running agent
            // that crashed at minute 5 would otherwise stay IsExecuting=true
            // forever.
            if (thread.PendingUserMessage is { Length: > 0 }
                && !string.IsNullOrEmpty(thread.ActiveMessageId))
            {
                logger?.LogInformation(
                    "[ThreadExec] Recovery: skipping {ThreadPath} — auto-execute candidate (pending msg set)",
                    threadPath);
                return;
            }

            logger?.LogInformation("[ThreadExec] Recovery: stale execution on {ThreadPath}, activeMsg={ActiveMsg}",
                threadPath, thread.ActiveMessageId);

            // Cancel pending tool calls on the active response message.
            // For delegation tool calls, check if the sub-thread actually completed.
            if (!string.IsNullOrEmpty(thread.ActiveMessageId))
            {
                var responsePath = $"{threadPath}/{thread.ActiveMessageId}";
                var responseMsgId = thread.ActiveMessageId!;
                var mainEntity = threadNode.MainNode ?? threadPath;

                // Mark all pending tool calls as cancelled — no query needed.
                // Sub-thread recovery happens independently on their own hub init.
                var updatedToolCalls = thread.StreamingToolCalls?
                    .Select(tc => tc.Result != null
                        ? tc
                        : tc with { Result = "Cancelled (server restarted)", IsSuccess = false })
                    .ToImmutableList() ?? ImmutableList<ToolCallEntry>.Empty;

                UpdateResponseCell(cache, responsePath, threadPath, responseMsgId, mainEntity,
                    msg => msg with
                    {
                        Text = msg.Text ?? "",
                        ToolCalls = updatedToolCalls,
                        Status = ThreadMessageStatus.Cancelled,
                        CompletedAt = DateTime.UtcNow
                    },
                    logger);
            }

            // Clear thread execution state — recovery runs on the thread hub
            // itself, so this is an OWN write via its workspace.
            workspace.GetMeshNodeStream().Update(node =>
            {
                var t = node.Content as Thread ?? new Thread();
                var cancelledAt = DateTime.UtcNow;
                return node with
                {
                    LastModified = cancelledAt,
                    Content = t with
                    {
                        Status = ThreadExecutionStatus.Idle,
                        ExecutionStatus = null,
                        ActiveMessageId = null,
                        TokensUsed = 0,
                        ExecutionStartedAt = null,
                        StreamingText = null,
                        StreamingToolCalls = null
                    }
                };
            }).Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "RecoverStaleExecutingThread: UpdateMeshNode failed for {ThreadPath}", threadPath));

            logger?.LogInformation("[ThreadExec] Recovery: cleared stale execution on {ThreadPath}", threadPath);
        });
    }

    /// <summary>
    /// Startup auto-execute hook for threads created with <c>BuildThreadWithMessages</c>:
    /// IsExecuting=true + PendingUserMessage set at creation time. Creates the user/response
    /// cells and dispatches the same SubmitMessageRequest path the GUI uses, so a single
    /// <c>CreateNodeRequest</c> can atomically create a thread AND start its first round.
    /// HandleSubmitMessage handles all client-initiated execution after startup.
    /// </summary>
    private static void WatchForExecution(IMessageHub hub)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        var threadPath = hub.Address.Path;

        // Startup auto-execute hook for threads created with BuildThreadWithMessages
        // (IsExecuting=true + PendingUserMessage set at creation time). Wait for the
        // FIRST emission whose Content is a MeshThread carrying both flags — earlier
        // emissions during data-source init can be empty/null and were previously
        // swallowed by Take(1), causing the auto-execute to silently drop.
        // HandleSubmitMessage handles runtime execution after startup.
        IObservable<MeshNode> ownNode;
        try { ownNode = hub.GetWorkspace().GetMeshNodeStream(); }
        catch { return; }

        // Take the FIRST emission whose Content is typed as MeshThread (post-
        // ResolveJsonElementContent). Previously we used `.Take(1)` directly on
        // the full stream — which races: an early emission during data-source init
        // can be null/JsonElement-shaped, the pattern match below fails silently,
        // and the auto-execute is dropped.
        //
        // We must NOT also wait for `PendingUserMessage` here: HandleSubmitMessage
        // sets PendingUserMessage AFTER hub init, and that update would re-trigger
        // a Where-based filter — racing HandleSubmitMessage's own dispatch and
        // double-creating cells (failing with "Node already exists").
        //
        // So: wait only for typed MeshThread Content; check PendingUserMessage
        // INSIDE Subscribe so threads NOT created with BuildThreadWithMessages
        // skip the auto-execute path silently.
        var sub = ownNode
            .Where(node => node?.Content is MeshThread)
            .Take(1)
            .Subscribe(node =>
        {
            // Diagnostic: every thread node we observe at this entry point.
            // Confirms the auto-execute watcher actually fires and lets us see
            // why threads created via BuildThreadWithMessages with PendingUserMessage
            // either dispatch or get skipped.
            if (node?.Content is MeshThread t0)
            {
                logger?.LogInformation(
                    "[ThreadExec] WatchForExecution observed {ThreadPath} status={Status} hasPendingMsg={HasPending} isExecuting={Executing} activeMsg={Active}",
                    threadPath, t0.Status, t0.PendingUserMessage is { Length: > 0 },
                    t0.IsExecuting, t0.ActiveMessageId ?? "(null)");
            }

            if (node?.Content is not MeshThread { PendingUserMessage: not null } thread)
                return;

            // Only auto-execute threads created with BuildThreadWithMessages
            if (!thread.IsExecuting || thread.ActiveMessageId == null)
                return;

            var responseMsgId = thread.ActiveMessageId;
            var responsePath = $"{threadPath}/{responseMsgId}";
            var activeIdx = thread.Messages.IndexOf(responseMsgId);
            var userMsgId = activeIdx > 0 ? thread.Messages[activeIdx - 1] : null;
            // MainNode for child cells = the thread's own MainNode (content node).
            var mainEntity = node?.MainNode ?? thread.PendingContextPath ?? threadPath;

            logger?.LogInformation(
                "[ThreadExec] Auto-execute (initial submit / BuildThreadWithMessages): {ThreadPath}, activeMsg={ActiveMsg}, pendingMsgLen={Len}, agent={Agent}",
                threadPath, responseMsgId, thread.PendingUserMessage?.Length ?? 0, thread.PendingAgentName ?? "(default)");

            var accessService = hub.ServiceProvider.GetService<AccessService>();
            if (!string.IsNullOrEmpty(thread.CreatedBy))
                accessService?.SetContext(new AccessContext { ObjectId = thread.CreatedBy, Name = thread.CreatedBy });

            var userCtx = !string.IsNullOrEmpty(thread.CreatedBy)
                ? new AccessContext { ObjectId = thread.CreatedBy, Name = thread.CreatedBy }
                : null;

            void StartExecution()
            {
                var startCache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
                UpdateResponseCell(startCache, responsePath, threadPath, responseMsgId,
                    mainEntity,
                    msg => msg with { Text = "Allocating agent...", Status = ThreadMessageStatus.Streaming },
                    logger);

                var executionHub = hub.GetHostedHub(
                    new Address($"{hub.Address}/_Exec"),
                    config => config,
                    HostedHubCreation.Always);

                // Direct method call — no SubmitMessageRequest post.
                ExecuteMessageAsync(executionHub!, new RoundParams(
                    ThreadPath: threadPath,
                    ResponseMessageId: responseMsgId,
                    UserMessageId: userMsgId,
                    UserMessageText: thread.PendingUserMessage ?? "",
                    AgentName: thread.PendingAgentName,
                    ModelName: thread.PendingModelName,
                    ContextPath: thread.PendingContextPath ?? thread.CreatedBy,
                    Attachments: thread.PendingAttachments
                ), userCtx);
            }

            // Create cells, then start execution
            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            meshService.CreateNode(new MeshNode(responseMsgId, threadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = mainEntity,
                Content = new ThreadMessage
                {
                    Role = "assistant", Text = "", Timestamp = DateTime.UtcNow,
                    Type = ThreadMessageType.AgentResponse,
                    AgentName = thread.PendingAgentName, ModelName = thread.PendingModelName
                }
            }).Subscribe(_ => StartExecution(),
                error =>
                {
                    logger?.LogDebug("[ThreadExec] Response cell creation error: {Error}", error.Message);
                    StartExecution();
                });

            if (userMsgId != null)
            {
                meshService.CreateNode(new MeshNode(userMsgId, threadPath)
                {
                    NodeType = ThreadMessageNodeType.NodeType, MainNode = mainEntity,
                    Content = new ThreadMessage
                    {
                        Role = "user", Text = thread.PendingUserMessage ?? string.Empty, Timestamp = DateTime.UtcNow,
                        Type = ThreadMessageType.ExecutedInput, CreatedBy = thread.CreatedBy
                    }
                }).Subscribe(_ => { },
                    error => logger?.LogDebug("[ThreadExec] User cell creation error: {Error}", error.Message));
            }
        });
        hub.RegisterForDisposal(sub);
    }

    /// <summary>
    /// Handler for <see cref="StartExecutionTrigger"/> on the <c>_Exec</c>
    /// hosted hub. Executes one full pre-stream step of the round:
    /// drains <see cref="MeshThread.PendingUserMessages"/> into
    /// <see cref="MeshThread.Messages"/>, materialises the user satellite cells,
    /// allocates a single response cell for the round, transitions
    /// <c>Status: StartingExecution → Executing</c>, then posts
    /// <see cref="SubmitMessageRequest"/> to itself so the existing
    /// <see cref="ExecuteMessageAsync"/> handler picks up the streaming pass.
    ///
    /// <para><b>Hub topology.</b> Posted by the thread hub's
    /// <see cref="ThreadSubmission.HandleStartExecution"/> after a successful
    /// atomic claim (Status flipped Idle → StartingExecution). The atomic claim
    /// stays on the thread hub because writes to the thread node serialise
    /// naturally through its single data-source action block. The drain + cell
    /// creation runs HERE on <c>_Exec</c> — the "execution does the move from
    /// pending to Messages" rule.</para>
    ///
    /// <para><b>Reads/writes go through <see cref="IMeshNodeStreamCache"/></b>,
    /// not <c>parentHub.GetWorkspace()</c>. The cache is the canonical path for
    /// non-owning hubs: one shared handle per path opened on the mesh hub,
    /// routing all updates through cross-hub messaging instead of touching the
    /// thread hub's data source synchronously. This eliminates the deadlock
    /// risk of <c>UpdateOwn</c>-on-parent-from-a-different-hub-thread and
    /// keeps every reader (watcher, GUI, MCP) seeing the same stream.</para>
    /// </summary>
    /// <summary>
    /// Handles <see cref="DelegationDispatchedTrigger"/> on the parent thread hub —
    /// runs inside the action block, serialized with all other hub operations.
    /// EmitDelegationEvent + sub-thread stream subscription wiring happen here so
    /// the AgentChatClient state mutation doesn't race with hub-pipeline reads.
    /// </summary>
    internal static IMessageDelivery HandleDelegationDispatched(
        IMessageHub hub, IMessageDelivery<DelegationDispatchedTrigger> delivery)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        var chat = hub.Get<AgentChatClient>();
        if (chat is null)
        {
            logger?.LogWarning(
                "[DelegationDispatched] no AgentChatClient on hub {Hub}; skipping CallId={CallId} sub={Path}",
                hub.Address, delivery.Message.CallId, delivery.Message.SubThreadPath);
            return delivery.Processed();
        }

        var callId = delivery.Message.CallId;
        var subThreadPath = delivery.Message.SubThreadPath;
        logger?.LogInformation(
            "[DelegationDispatched] hub={Hub} EMIT_DISPATCHED sub={Path}", hub.Address, subThreadPath);
        chat.EmitDelegationEvent(
            new MeshWeaver.AI.Delegation.DelegationEvent(callId, subThreadPath,
                MeshWeaver.AI.Delegation.DelegationLifecycle.Dispatched));

        // Sub-thread stream subscription — on Running→Idle, post Terminal trigger
        // back to THIS hub so the EmitDelegationEvent(Terminal) also runs serialised.
        var workspace = hub.GetWorkspace();
        var sawRunning = false;
        workspace.GetMeshNodeStream(subThreadPath).Subscribe(
            node =>
            {
                if (node?.Content is not MeshThread t) return;
                if (t.Status is ThreadExecutionStatus.Executing
                             or ThreadExecutionStatus.StartingExecution
                             or ThreadExecutionStatus.Completing)
                {
                    sawRunning = true;
                }
                else if (sawRunning && t.Status == ThreadExecutionStatus.Idle)
                {
                    hub.Post(
                        new DelegationTerminalTrigger(callId, subThreadPath),
                        o => o.WithTarget(hub.Address));
                }
            },
            ex => logger?.LogWarning(ex,
                "[DelegationDispatched] sub-thread stream errored sub={Path}", subThreadPath));

        return delivery.Processed();
    }

    /// <summary>
    /// Handles <see cref="DelegationTerminalTrigger"/> on the parent thread hub.
    /// Emits the Terminal delegation event so the cancel-watcher + tool-call
    /// stamper drop the entry. Runs inside the action block.
    /// </summary>
    internal static IMessageDelivery HandleDelegationTerminal(
        IMessageHub hub, IMessageDelivery<DelegationTerminalTrigger> delivery)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        var chat = hub.Get<AgentChatClient>();
        if (chat is null) return delivery.Processed();

        var callId = delivery.Message.CallId;
        var subThreadPath = delivery.Message.SubThreadPath;
        logger?.LogInformation(
            "[DelegationTerminal] hub={Hub} EMIT_TERMINAL sub={Path}", hub.Address, subThreadPath);
        chat.EmitDelegationEvent(
            new MeshWeaver.AI.Delegation.DelegationEvent(callId, subThreadPath,
                MeshWeaver.AI.Delegation.DelegationLifecycle.Terminal));
        return delivery.Processed();
    }

    internal static IMessageDelivery HandleStartExecutionOnExec(
        IMessageHub execHub,
        IMessageDelivery<StartExecutionTrigger> delivery)
    {
        var parentHub = execHub.Configuration.ParentHub
            ?? throw new InvalidOperationException(
                "_Exec hosted hub has no ParentHub — cannot resolve thread path");
        var logger = execHub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        var threadPath = delivery.Message.ThreadPath;

        if (!string.Equals(parentHub.Address.Path, threadPath, StringComparison.Ordinal))
        {
            logger?.LogWarning(
                "[HandleStartExecutionOnExec] trigger landed on wrong _Exec hub: parent={Parent} threadPath={ThreadPath}",
                parentHub.Address.Path, threadPath);
            return delivery.Processed();
        }

        // Read the post-claim state through the shared mesh-node cache (single
        // source of truth across all readers). The cache hydrates from the
        // thread hub's stream via the mesh hub's workspace — one async hop
        // behind the thread hub's own emissions. Without the Where guard,
        // .Take(1) races and routinely returns the PRE-claim snapshot
        // (Status=Idle) — the post-claim StartingExecution state hasn't yet
        // propagated to the mesh-hub cache. The old code then bailed on the
        // status check ("status=Idle — drop trigger") and the watcher had
        // no reason to re-fire (Status was non-Idle on the thread hub),
        // wedging the thread in StartingExecution forever. Symptom: every
        // ThreadSubmissionIntegrationTest.Submit_* test hangs with
        // IsExecuting=true / Messages=[].
        //
        // Fix: wait for the cache to receive an emission whose Status is one
        // of the post-claim states. StartingExecution is the expected one
        // (set by HandleStartExecution's claim). Executing / Completing /
        // Idle / Done are not — they'd be a stale or out-of-order emission
        // that the bail check should still drop.
        var cache = execHub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        cache.GetStream(threadPath)
            .Where(n => (n?.Content as MeshThread)?.Status == ThreadExecutionStatus.StartingExecution)
            .Take(1)
            // 30 s, not 10 s — Orleans cold-start grain activation + cache hydration
            // can run 15-25 s on a contended CI silo. The prior 10 s tripped on
            // every Orleans.Test that goes through chat (delegation, portal flow,
            // export, thread access — 8+ failures in CI run 26376715753 all
            // showed `[HandleStartExecutionOnExec] cache read failed`).
            .Timeout(TimeSpan.FromSeconds(30))
            .Subscribe(
                latest =>
                {
                    if (latest?.Content is not MeshThread thread)
                    {
                        logger?.LogWarning(
                            "[HandleStartExecutionOnExec] thread node has no MeshThread content for {ThreadPath}",
                            threadPath);
                        return;
                    }
                    // Where filter guarantees Status==StartingExecution; redundant
                    // assert kept for defensive logging if the predicate is ever
                    // changed and a stale snapshot slips through.
                    if (thread.Status != ThreadExecutionStatus.StartingExecution)
                    {
                        logger?.LogDebug(
                            "[HandleStartExecutionOnExec] status={Status} (not StartingExecution) — drop trigger for {ThreadPath}",
                            thread.Status, threadPath);
                        return;
                    }
                    // Set the access context for this round INSIDE _Exec so
                    // every downstream Post (cell creation, agent calls,
                    // workspace writes via the cache) is stamped with the
                    // right user identity. Resolve from the thread node's
                    // CreatedBy — the watcher fires on a stream scheduler
                    // where AsyncLocal carries no useful identity. The
                    // delivery's AccessContext is the secondary source (set
                    // by the framework when the thread hub posted the
                    // trigger).
                    var accessService = execHub.ServiceProvider.GetService<MeshWeaver.Messaging.AccessService>();
                    var userCtx = delivery.AccessContext
                        ?? (!string.IsNullOrEmpty(thread.CreatedBy)
                            ? new MeshWeaver.Messaging.AccessContext { ObjectId = thread.CreatedBy!, Name = thread.CreatedBy! }
                            : null);
                    if (accessService != null && userCtx != null)
                    {
                        accessService.SetContext(userCtx);
                        logger?.LogDebug(
                            "[HandleStartExecutionOnExec] access context set: {User} for {ThreadPath}",
                            userCtx.ObjectId, threadPath);
                    }
                    // Step B + C live in DispatchAfterClaim → DispatchRound,
                    // which posts CreateNodeRequest for the user/response
                    // cells under this AccessContext, and writes the commit
                    // through IMeshNodeStreamCache.
                    //
                    // 🚨 onFailure rolls the thread back to Status=Idle so the
                    // submission watcher's next emission sees a clean state and
                    // re-dispatches. Without this, ANY error in user-cell or
                    // response-cell creation OR the round-commit UpdateMeshNode
                    // leaves the thread stuck at Status=StartingExecution
                    // forever (the prod symptom 2026-05-21 with the
                    // add-markus-kleiner thread). Post-deploy silo restarts +
                    // CreateNodeRequest delivery failures are the typical
                    // triggers. The guard inside the Update lambda prevents
                    // clobbering if another actor already moved Status forward.
                    ThreadSubmissionServer.DispatchAfterClaim(parentHub, latest, logger,
                        onFailure: () =>
                        {
                            logger?.LogWarning(
                                "[HandleStartExecutionOnExec] DispatchAfterClaim failed for {ThreadPath} — rolling Status back to Idle so the submission watcher can re-dispatch",
                                threadPath);
                            cache.Update(threadPath, node =>
                            {
                                if (node?.Content is not MeshThread t
                                    || t.Status != ThreadExecutionStatus.StartingExecution)
                                    return node!;
                                return node with
                                {
                                    Content = t with
                                    {
                                        Status = ThreadExecutionStatus.Idle,
                                        ActiveMessageId = null,
                                        ExecutionStartedAt = null
                                    }
                                };
                            }).Subscribe(
                                _ => { },
                                ex => logger?.LogWarning(ex,
                                    "[HandleStartExecutionOnExec] Rollback to Idle failed for {ThreadPath}",
                                    threadPath));
                        });
                },
                ex => logger?.LogWarning(ex,
                    "[HandleStartExecutionOnExec] cache read failed for {ThreadPath}", threadPath));

        return delivery.Processed();
    }

    // HandleSubmitMessage + HandleSubmitMessageLegacy deleted 2026-05-25.
    // Public submissions go through ThreadSubmission.Submit → ThreadInput.AppendUserInput
    // → the submission watcher. _Exec calls ExecuteMessageAsync directly (method, not message).

    /// <summary>
    /// Async handler on the _Exec hosted hub.
    /// Prepares agent and await-streams the response.
    /// Uses UpdateMeshNode on a remote stream to push text to the response node.
    ///
    /// User input received while a round is in progress is held in
    /// <see cref="MeshThread.PendingUserMessages"/>. The submission watcher dispatches
    /// a NEW round (with its own response cell) as soon as this one completes — so
    /// follow-up typed input is naturally queued without cancelling the current
    /// model turn. Mid-iteration drain (injecting new user input into the same
    /// response without round-boundary tear-down) would require manually orchestrating
    /// the tool loop instead of relying on Microsoft.Extensions.AI's auto-invocation;
    /// that's intentionally NOT done here.
    /// </summary>
    /// <summary>
    /// Parameters for a single agent round. Direct-call replacement for the
    /// old <c>SubmitMessageRequest</c> wire message — the inputs the agent loop
    /// needs to run one round. Not a wire message: ExecuteMessageAsync is
    /// invoked as a method, not via Post/handler dispatch.
    /// </summary>
    internal sealed record RoundParams(
        string ThreadPath,
        string ResponseMessageId,
        string? UserMessageId,
        string UserMessageText,
        string? AgentName,
        string? ModelName,
        string? ContextPath,
        IReadOnlyList<string>? Attachments);

    internal static void ExecuteMessageAsync(
        IMessageHub hub,
        RoundParams request,
        AccessContext? userAccessContext)
    {
        var parentHub = hub.Configuration.ParentHub!;
        var threadPath = request.ThreadPath;
        var logger = parentHub.ServiceProvider.GetRequiredService<ILogger<AgentChatClient>>();
        var cache = parentHub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var responseMsgId = request.ResponseMessageId
            ?? throw new InvalidOperationException(
                $"ExecuteMessageAsync: RoundParams for thread {threadPath} has no ResponseMessageId");
        var responsePath = $"{threadPath}/{responseMsgId}";

        // Helper: push content to the response message via IMeshNodeStreamCache.
        // 🚨 Same shared handle that the GUI's ThreadMessageBubbleView reads from —
        // single upstream subscription process-wide. Replaces the per-_Exec
        // workspace.GetRemoteStream that opened a separate handle (writes through
        // one were invisible to readers of the other).
        var mainEntity = request.ContextPath ?? threadPath;
        // Heartbeat write throttle — stamp LastActivityAt on the OWN thread node
        // at most once per heartbeatStampInterval. Reads in the closure run on
        // _Exec's serialized action block, so a plain DateTime field is safe.
        // 1 s matches the heartbeat scanner cadence so a fresh delta is always
        // visible to the scanner before its next tick.
        var lastActivityStamped = DateTime.MinValue;
        var heartbeatStampInterval = TimeSpan.FromSeconds(1);
        // Returns the cache.Update IObservable so terminal-status callers can
        // AWAIT the write before signalling round completion — without this,
        // the test base's quiesce phase trips on the in-flight DataChangeRequest
        // Observe callbacks ("9 pending callback(s) after 0.50s" in
        // DelegationWriteCountTest). Streaming-chunk callers still
        // Subscribe(...) fire-and-forget for perf.
        IObservable<MeshNode> PushToResponseMessage(string text, ImmutableList<ToolCallEntry> toolCalls,
            ImmutableList<NodeChangeEntry> updatedNodes,
            string? agentName, string? modelName,
            int? inputTokens = null, int? outputTokens = null, int? totalTokens = null,
            DateTime? completedAt = null,
            ThreadMessageStatus? status = null,
            string? summary = null)
        {
            logger.LogDebug("[ThreadExec] PUSH_TO_MSG: responsePath={ResponsePath}, textLen={TextLen}, toolCalls={ToolCalls}, updatedNodes={UpdatedNodes}, status={Status}",
                responsePath, text.Length, toolCalls.Count, updatedNodes.Count, status?.ToString() ?? "(preserve)");

            var updateObs = cache.Update(responsePath, node =>
            {
                var current = node?.Content as ThreadMessage ?? new ThreadMessage
                {
                    Role = "assistant",
                    Text = "",
                    Type = ThreadMessageType.AgentResponse,
                    Status = ThreadMessageStatus.Streaming
                };
                // Status: once terminal (Completed/Cancelled/Error), no patch can
                // regress to Streaming. Sample(100ms) emits its last buffered value
                // on source completion (snapshots.OnCompleted), and that emission's
                // callback writes Status=Streaming — if it lands after the final
                // success/cancel/error push it would flip the UI back to "still
                // running" until the next render. Visible as flickering at the end
                // of the response.
                var requestedStatus = status ?? current.Status;
                var nextStatus = current.Status is ThreadMessageStatus.Completed
                                                or ThreadMessageStatus.Cancelled
                                                or ThreadMessageStatus.Error
                                 && requestedStatus == ThreadMessageStatus.Streaming
                    ? current.Status
                    : requestedStatus;
                // 🚨 ToolCalls merge: ExecuteDelegationAsync.StampTerminalOnParentToolCall
                // writes Result+Status+DelegationPath onto delegation entries via
                // cache.Update concurrently with this streaming loop. If we replaced
                // ToolCalls wholesale with `toolCalls` (the in-memory toolCallLog),
                // the final iteration of this loop would CLOBBER the terminal stamp
                // because toolCallLog only carries DelegationPath, never Result.
                // Merge by DelegationPath (or Name+CallId match) — keep whichever
                // entry has Result populated. The cache's current state wins for
                // entries that have already terminated; toolCallLog wins for
                // entries still mid-flight.
                var mergedToolCalls = MergeToolCallEntries(current.ToolCalls, toolCalls);
                // 🚨 Text monotonic-growth guard: streaming + tool-call mid-stream
                // writes both flow through this Update path. A tool-call patch
                // that doesn't carry the latest text (because the caller built
                // its `text` from a stale snapshot of the streamed-so-far buffer
                // BEFORE more tokens arrived) would shrink the field — visible
                // to UI subscribers as a flicker / regression. While Status is
                // terminal-locked above, Text is otherwise free to shrink. Cap:
                // while Status is Streaming, only allow grow OR same length.
                // Once terminal, the final text from the streaming loop's
                // completion is the source of truth — let it through.
                var nextText = nextStatus == ThreadMessageStatus.Streaming
                               && text.Length < current.Text.Length
                    ? current.Text
                    : text;
                var updatedContent = current with
                {
                    Text = nextText,
                    ToolCalls = mergedToolCalls,
                    UpdatedNodes = updatedNodes,
                    AgentName = agentName ?? current.AgentName,
                    ModelName = modelName ?? current.ModelName,
                    InputTokens = inputTokens ?? current.InputTokens,
                    OutputTokens = outputTokens ?? current.OutputTokens,
                    TotalTokens = totalTokens ?? current.TotalTokens,
                    CompletedAt = completedAt ?? current.CompletedAt,
                    Status = nextStatus,
                    Summary = summary ?? current.Summary
                };
                return node != null
                    ? node with { Content = updatedContent }
                    : new MeshNode(responseMsgId, threadPath)
                    {
                        NodeType = ThreadMessageNodeType.NodeType,
                        MainNode = mainEntity,
                        Content = updatedContent
                    };
            });

            // Streaming hot-path callers Subscribe(...) fire-and-forget; the
            // hot observable lets terminal-status callers await via FirstAsync.
            updateObs.Subscribe(
                _ => { },
                ex => logger.LogWarning(ex,
                    "[ThreadExec] cache.Update failed for {Path}", responsePath));

            // Heartbeat stamp on the OWN thread. Throttled to one write per
            // heartbeatStampInterval so the streaming hot path (Sample(100ms))
            // doesn't spam thread-node writes — one per interval is plenty
            // since the heartbeat scanner runs every 5 s and the timeout is
            // 30 s. Single source of "is this sub-thread still alive?" the
            // parent's heartbeat scanner reads via cache.GetStream(threadPath).
            var now = DateTime.UtcNow;
            if (now - lastActivityStamped > heartbeatStampInterval)
            {
                lastActivityStamped = now;
                parentHub.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
                {
                    if (node?.Content is not MeshThread t) return node!;
                    return node with { Content = t with { LastActivityAt = now } };
                }).Subscribe(
                    _ => { },
                    ex => logger.LogDebug(ex,
                        "[ThreadExec] LastActivityAt stamp failed for {Path}", threadPath));
            }

            return updateObs;
        }

        var execLogger = parentHub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.ThreadExecution");
        // Write thread state from THIS HUB (parentHub = thread hub), not via
        // the mesh-hub-backed cache. With delta-based PatchDataRequest in
        // MeshNodeStreamHandle.UpdateRemote, concurrent writers from different
        // mirrors no longer clobber each other — so the cache routing that
        // forced writes through the mesh hub (losing caller identity, surfacing
        // 'no AccessContext' warnings, sender=mesh) is obsolete. The owning
        // per-node hub remains the source of truth.
        // 🚨 IObservable surface (no internal Subscribe). Cold — the
        // stream.Update side effect runs once per Subscribe. Callers MUST
        // Subscribe (the void-style fire-and-forget callsites Subscribe with
        // a default `(_ => {}, ex => log)`; terminal-phase callers chain via
        // SelectMany / continuation to wait for the commit before signalling
        // round completion). Per AsynchronousCalls.md: never bridge to Task,
        // always compose into the observable chain.
        IObservable<MeshNode> UpdateThreadExecution(Func<MeshThread, MeshThread> mutate) =>
            parentHub.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
            {
                // 🚨 No silent fallback. This Update runs on the parent thread
                // hub's own action block; by the time this lambda fires, the
                // node MUST be initialized (the hub's data source loaded it
                // before processing the patch). A null Content here means the
                // hub was processing this write before its node hydrated —
                // surface loudly so the load order is fixed at the source.
                if (node.Content is not MeshThread thread)
                    throw new InvalidOperationException(
                        $"UpdateThreadExecution: thread node {threadPath} has Content of type "
                        + $"{node.Content?.GetType().Name ?? "<null>"}, not MeshThread. "
                        + "The hub must be fully initialized before terminal-state writes.");
                return node with { Content = mutate(thread) };
            });


        // Set user access context
        var accessService = parentHub.ServiceProvider.GetService<AccessService>();
        if (userAccessContext != null)
            accessService?.SetContext(userAccessContext);

        // Reuse cached agent (skips 3+ seconds of agent init on 2nd+ message).
        // hub.Get<T> / hub.Set<T> is per-hub instance state — same hub across
        // rounds = same cached client. Cache miss = resume after restart:
        // load prior user messages from the persisted thread (excluding the
        // current submission, which request.UserMessageText already carries),
        // construct the client with that history, cache it, and proceed.
        var cachedClient = parentHub.Get<AgentChatClient>();
        IObservable<AgentChatClient> clientObs;
        if (cachedClient != null)
        {
            clientObs = Observable.Return(cachedClient);
        }
        else
        {
            // 🚨 New threads start with empty conversation history — no
            // bootstrap query needed. The per-round
            // <see cref="LoadFullConversationHistoryFromMesh"/> below loads
            // ALL prior cells (user + assistant) per round, so resume after
            // restart still gets full context. Skipping the cache-miss
            // bootstrap query takes ~2s off cold-start latency on a brand-
            // new thread (formerly: "Loading conversation history..."
            // placeholder + 10s-timeout IMeshQueryCore.ObserveQuery scan).
            var c = new AgentChatClient(parentHub.ServiceProvider, priorMessages: null);
            c.SetThreadId(threadPath);
            parentHub.Set(c);
            clientObs = Observable.Return(c);
        }

        var initSub = clientObs.Take(1).Subscribe(chatClient =>
        {
            // Initialize is sync (binds the chat client to the workspace's shared
            // synced agent collection). Wait for the first WhenInitialized
            // emission — synchronous when the synced query is warm-cached, async
            // on first cold load — before starting the streaming loop.
            //
            // 🚨 Fail-fast on stall: if the synced agent query never emits
            // (root cause of the prod sub-thread deadlock on 2026-05-20 —
            // agent subscription on the sub-thread workspace stalled silently),
            // surface the failure within 60s. NOT a Timeout(default) fallback
            // (that wipes state — see feedback_timeout_wipes_synced_state) —
            // an ERROR Timeout that flips the thread back to Idle and clears
            // ActiveMessageId so the UI unsticks instead of perpetually
            // "executing". 60s is generous; the workspace-cached synced
            // query should emit Initial within seconds even on cold start.
            chatClient.Initialize(request.ContextPath, request.ModelName);
            chatClient.WhenInitialized
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(60))
                .Subscribe(client =>
                {
                logger.LogDebug("[ThreadExec] Agents ready for {ThreadPath}, starting execution", threadPath);

                // Set context from remote stream — must subscribe (Current is null on cold streams).
                // When ContextPath is empty we just set null; otherwise wait for the first emission
                // (with a short timeout fallback so a missing/inaccessible node doesn't stall execution)
                // before continuing with SetExecutionContext + history load.
                IObservable<MeshNode?> contextNodeObs;
                if (!string.IsNullOrEmpty(request.ContextPath))
                {
                    // Read context node via cache (shared upstream, no per-_Exec handle).
                    contextNodeObs = cache.GetStream(request.ContextPath)
                        .Select(n => (MeshNode?)n)
                        .Where(v => v != null)
                        .Take(1)
                        .Timeout(TimeSpan.FromSeconds(5))
                        .Catch<MeshNode?, Exception>(ex =>
                        {
                            logger.LogWarning(ex,
                                "[ThreadExec] Failed to load context node {ContextPath}; proceeding with null Node",
                                request.ContextPath);
                            return Observable.Return<MeshNode?>(null);
                        });
                }
                else
                {
                    contextNodeObs = Observable.Return<MeshNode?>(null);
                }

                contextNodeObs.Subscribe(contextNode =>
                {
                    if (!string.IsNullOrEmpty(request.ContextPath))
                    {
                        client.SetContext(new AgentContext
                        {
                            Address = new Address(request.ContextPath),
                            Context = request.ContextPath,
                            Node = contextNode
                        });
                    }

                if (!string.IsNullOrEmpty(request.AgentName))
                    client.SetSelectedAgent(request.AgentName);
                if (request.Attachments is { Count: > 0 })
                    client.SetAttachments(request.Attachments);

                // userAccessContext already in scope from the method parameter.
                client.SetExecutionContext(new ThreadExecutionContext
                {
                    ThreadPath = threadPath,
                    ResponseMessageId = responseMsgId,
                    ContextPath = request.ContextPath,
                    UserAccessContext = userAccessContext
                });

                // 🚨 Load FULL prior conversation (user + assistant) per round.
                // Real LLMs (Anthropic, OpenAI) don't share session state across
                // SubmitMessageRequest deliveries, and Echo (used by tests)
                // doesn't track history at all. So if every round only sent the
                // new user message, the agent would never see prior turns —
                // ChatHistoryTest catches exactly this regression.
                LoadFullConversationHistoryFromMesh(parentHub, threadPath,
                        excludeUserMessageId: request.UserMessageId,
                        excludeResponseMessageId: responseMsgId,
                        logger)
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(5))
                    .Catch<IReadOnlyList<ChatMessage>, Exception>(ex =>
                    {
                        // 🚨 LOUD log — history-load failure means the agent sees
                        // truncated context (or nothing) for this round. Continue with
                        // empty so the round doesn't wedge, but surface the failure so
                        // CI surfaces the actual cause (per-cell timeout / stream error)
                        // instead of producing a wrong-content assertion downstream.
                        logger.LogError(ex,
                            "[ThreadExec] HISTORY_LOAD_FAILED threadPath={ThreadPath} — proceeding with EMPTY history; agent will see only the new user message",
                            threadPath);
                        return Observable.Return<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
                    })
                    .Subscribe(history =>
                {
                    var chatHistory = history.ToImmutableList();

                    var toolCallLog = ImmutableList<ToolCallEntry>.Empty;
                var nodeChangeLog = ImmutableList<NodeChangeEntry>.Empty;
                // toolCallLog + nodeChangeLog are mutated from 4 concurrent paths:
                //   1. Streaming loop (Task.Run with await foreach over agent updates)
                //   2. FCC middleware (ChatClientAgentFactory's .Use(...) callback,
                //      fires on FCC's invocation thread)
                //   3. client.ForwardToolCall (alias for path 2 on test agents that
                //      bypass FCC)
                //   4. client.UpdateDelegationStatus (sub-thread completion callback
                //      on the sub-thread hub's grain scheduler)
                // Without a lock, the read-modify-write idiom
                //   `toolCallLog = toolCallLog.Select/Add/SetItem(...)`
                // suffers (a) lost updates — the flapping "delegations=0/1"
                // alternation in OrleansDelegationTest's STREAM log — and
                // (b) Index-out-of-range when one thread captures idx via FindIndex
                // and a concurrent thread reassigns the list between FindIndex and
                // SetItem (the line 167 InvalidOperationException). Repro:
                // OrleansDelegationTest.Delegation_ToolCallsAppear_WithDelegationPath
                // failed intermittently before this lock.
                var logLock = new object();
                // responseText is captured after InvokeAsync creates it (see below)
                StringBuilder? capturedResponseText = null;
                client.ForwardNodeChange = entry => { lock (logLock) { nodeChangeLog = nodeChangeLog.Add(entry); } };
                string? currentStatus = null;
                // Pending Dispatched paths: when the Dispatched event fires BEFORE
                // the outer streaming loop processes the corresponding
                // FunctionCallContent and adds the bare entry, we queue the path
                // here. The streaming loop drains the queue on each add. Without
                // this, fast FCC dispatches lose the stamp and the response cell
                // ships a bare delegate_to entry (the failing assertion in
                // DelegationWriteCountTest). Drained in FIFO order to preserve
                // multi-delegation correlation.
                var pendingDispatchedPaths = ImmutableQueue<string>.Empty;
                // Subscribe to delegation lifecycle events. On Dispatched, stamp
                // the path onto the first unmatched delegate_to tool-call entry
                // and push the response. Replaces the legacy UpdateDelegationStatus
                // callback (which keyed by display-name string). The subscription
                // is disposed in the finally block at end of the round.
                IDisposable? delegationStampSub = client is AgentChatClient ac
                    ? ac.Delegations
                        .Where(evt => evt.Phase == MeshWeaver.AI.Delegation.DelegationLifecycle.Dispatched)
                        .Subscribe(evt =>
                {
                    logger.LogInformation(
                        "[ThreadExec] DELEGATION_DISPATCHED: threadPath={ThreadPath}, subPath={SubPath}, callId={CallId}, toolCallLogSize={LogSize}",
                        threadPath, evt.SubThreadPath, evt.CallId, toolCallLog.Count);
                    var stamped = false;
                    ImmutableList<ToolCallEntry> snapshotLog;
                    ImmutableList<NodeChangeEntry> snapshotNodes;
                    string textSnapshot;
                    lock (logLock)
                    {
                        toolCallLog = toolCallLog.Select(e =>
                        {
                            if (!stamped && e.Name.StartsWith("delegate_to") && e.DelegationPath == null)
                            {
                                stamped = true;
                                logger.LogInformation("[ThreadExec] DELEGATION_STAMPED: name={Name} delPath={DelPath} callId={CallId}",
                                    e.Name, evt.SubThreadPath, evt.CallId);
                                return e with { DelegationPath = evt.SubThreadPath };
                            }
                            return e;
                        }).ToImmutableList();
                        if (!stamped)
                        {
                            // FCC fired Dispatched faster than the outer streaming
                            // loop could process the FunctionCallContent. Queue the
                            // path; the streaming-loop add will drain it.
                            pendingDispatchedPaths = pendingDispatchedPaths.Enqueue(evt.SubThreadPath);
                        }
                        snapshotLog = toolCallLog;
                        snapshotNodes = nodeChangeLog;
                        // StringBuilder.ToString() is NOT thread-safe with concurrent
                        // Append — guard with logLock (same primitive as every other
                        // toolCallLog mutation; otherwise mid-walk ToString throws
                        // ArgumentOutOfRangeException, the original OrleansDelegationTest
                        // line 167 failure).
                        textSnapshot = capturedResponseText?.ToString() ?? "";
                    }
                    if (!stamped)
                        logger.LogInformation("[ThreadExec] DELEGATION_DEFERRED_STAMP: subPath={SubPath} — queued for streaming-loop add (logSize={LogSize}, queueDepth={Q})",
                            evt.SubThreadPath, snapshotLog.Count, pendingDispatchedPaths.Count());
                    // Push immediately so the parent message shows the delegation
                    // link while the sub-thread executes (streaming loop is blocked
                    // during tool execution; throttle block never runs).
                    PushToResponseMessage(textSnapshot, snapshotLog, snapshotNodes,
                        request.AgentName, request.ModelName);
                        })
                    : null;
                // Middleware-side ForwardToolCall: UPDATES the matching bare entry
                // (added by the streaming branch's FunctionCallContent path) with the
                // result, instead of skipping. Previously this branch skipped the
                // result-bearing entry whenever a Result==null entry existed —
                // correct for production agents where the streaming branch's
                // FunctionResultContent handler later runs SetItem with the same
                // Result. But for delegations with our refactored
                // ExecuteDelegationAsync (yields text deltas, not FunctionResultContent
                // in the output stream) AND for test agents that bypass FCC's
                // FRC-in-output behaviour, the streaming branch's SetItem never fires
                // → Result stays null forever. Update-instead-of-skip closes that.
                // Production agents pay one redundant SetItem (no-op since the data is
                // identical); test/delegation agents finally get Result populated.
                client.ForwardToolCall = entry =>
                {
                    lock (logLock)
                    {
                        // SetItem-only — never Add. ForwardToolCall is the LATE
                        // mirror for an entry the streaming loop's FunctionCallContent
                        // branch (line 1346) already added. Adding here causes
                        // duplicates when the mirror fires before the streaming
                        // branch (FCC implementation dependent — some buffer FCC
                        // chunks until after tool invocation, some emit them
                        // synchronously).
                        //
                        // Match priority:
                        //  1) Same DelegationPath — covers ExecuteDelegationAsync's
                        //     StampTerminal mirror after UpdateDelegationStatus
                        //     already stamped DelegationPath on the bare entry.
                        //  2) Same Name + null Result — the standard "bare entry
                        //     waiting for completion" shape.
                        var idx = -1;
                        if (entry.DelegationPath is not null)
                            idx = toolCallLog.FindIndex(e => e.DelegationPath == entry.DelegationPath);
                        if (idx < 0)
                            idx = toolCallLog.FindIndex(e => e.Name == entry.Name && e.Result == null);
                        if (idx >= 0)
                        {
                            var existing = toolCallLog[idx];
                            // Merge: incoming carries the late updates (Result,
                            // Status, terminal Timestamp). Preserve existing's
                            // CallId + Arguments + DisplayName when the incoming
                            // entry doesn't carry them — the streaming branch
                            // had richer call-site detail. Critically, CallId
                            // must survive the SetItem so the streaming branch's
                            // CallId-keyed dedupe (alreadyByCallId) catches a
                            // re-emitted FunctionCallContent.
                            toolCallLog = toolCallLog.SetItem(idx, entry with
                            {
                                DelegationPath = entry.DelegationPath ?? existing.DelegationPath,
                                CallId = entry.CallId ?? existing.CallId,
                                Arguments = entry.Arguments ?? existing.Arguments,
                                DisplayName = entry.DisplayName ?? existing.DisplayName
                            });
                        }
                        // No-match case: the mirror fired before the streaming
                        // loop processed the FCC chunk for this tool call. Drop
                        // the entry — the streaming loop will eventually add a
                        // bare entry that the FCC FunctionResultContent handler
                        // will populate (line 1422). Adding here would duplicate.
                    }
                };

                var agentDisplayName = request.AgentName ?? "Agent";

                // Build full message list: prior history (loaded above, EXCLUDES the
                // current submission's user/response cells) + current user message.
                // The system prompt is added by AgentChatClient.GetStreamingResponseAsync
                // before forwarding to the inner IChatClient.
                var allMessages = chatHistory.Add(
                    new ChatMessage(ChatRole.User, request.UserMessageText));
                logger.LogInformation("[ThreadExec] Sending {Count} messages to agent ({HistoryCount} history + 1 new): threadPath={ThreadPath}, agent={Agent}",
                    allMessages.Count, chatHistory.Count, threadPath, request.AgentName ?? "(default)");

                logger.LogInformation("[ThreadExec] STREAMING_START: threadPath={ThreadPath}, responsePath={ResponsePath}",
                    threadPath, responsePath);
                // Run streaming on thread pool via Task.Run — the grain scheduler
                // stays FREE to process tool call responses, delegation callbacks, and
                // workspace updates. Without this, tool calls deadlock: they await a
                // response that needs the grain scheduler which is blocked by InvokeAsync.
                //
                // DelayDeactivation keeps the grain alive while the thread pool task runs.
                // BeginAsyncOperation signals the grain keep-alive timer.
                // After await Task.Run(...), execution returns to the grain scheduler.
                //
                // Reuse the CancellationTokenSource HandleSubmitMessage stored on the
                // parent hub. Storing it here would be too late — a Stop click between
                // SubmitMessageResponse arriving at the GUI and us reaching this point
                // would find a null CTS and silently no-op. If for some reason the slot
                // is empty (auto-execute via WatchForExecution doesn't go through
                // HandleSubmitMessage), allocate a fresh one as a safety net.
                var executionCts = parentHub.Get<CancellationTokenSource>()
                    ?? StoreNewCts(parentHub);
                // Cancel Task.Run when the hub disposes (grain deactivation).
                // Without this, OnDeactivateAsync waits up to 120s for the Task.Run
                // that's stuck on an AI API call with no cancellation signal.
                hub.RegisterForDisposal(_ => executionCts.Cancel());
                // Push progress: generating
                PushToResponseMessage("Generating response...", ImmutableList<ToolCallEntry>.Empty,
                    ImmutableList<NodeChangeEntry>.Empty, request.AgentName, request.ModelName,
                    status: ThreadMessageStatus.Streaming);

                // Streaming loop runs on the thread pool via Task.Run — the grain
                // scheduler stays FREE to process tool-call responses, delegation
                // callbacks, and workspace updates. Without this, tool calls
                // deadlock: they await a response that needs the grain scheduler
                // which is blocked by the in-flight `await foreach`. The
                // `await foreach` is the ONLY async place in the entire
                // implementation; tool invocations + cell pushes inside this
                // task run as observable composition, and the grain scheduler
                // is available to handle their cross-hub Subscribe callbacks.
                _ = Task.Run(async () =>
                {
                    // Re-seed user AccessContext at the task-launch boundary. Inside this lambda we
                    // run the streaming loop + tool calls + responseStream.Update, all of which
                    // post to other hubs. Preceded by a chain of Subscribe callbacks
                    // (Initialize, contextNodeObs, threadWorkspace.GetStream, history loaders) —
                    // each fires on the upstream hub's pipeline where AsyncLocal Context flips
                    // to the per-cell hub's impersonated address. Reseed here so every downstream
                    // post goes out under the user's identity.
                    if (userAccessContext != null)
                        parentHub.ServiceProvider.GetService<AccessService>()?.SetContext(userAccessContext);

                    var ct = executionCts.Token;
                    var responseText = new StringBuilder();
                    capturedResponseText = responseText;
                    int? inputTokens = null;
                    int? outputTokens = null;
                    int? totalTokens = null;

                    // No time-limit watchdog. A streaming session blocked on an
                    // unresponsive AI endpoint, a long-running delegation, or a
                    // sub-thread doing its own multi-minute work is indistinguishable
                    // from a "stuck" pipeline from the parent's perspective — and an
                    // arbitrary deadline that fires `executionCts.Cancel()` would
                    // tear those down even when something is happening down the tree.
                    // Manual cancellation via the Stop button (RequestedCancellationAt
                    // flip on the thread node, see RequestViaStreamUpdate.md) is the
                    // only legitimate cancel.

                    try
                    {
                    // Keep the grain alive during the entire execution — including tool calls
                    // and delegations where the streaming loop is blocked.
                    using var heartbeatSubscription = parentHub.BeginAsyncOperation();
                    using var snapshots = new Subject<StreamingSnapshot>();
                    using var pushSub = snapshots
                        .Sample(StreamingSampleInterval)
                        .Subscribe(s => PushToResponseMessage(
                            StripSummaryBlock(s.Text), s.ToolCalls, s.NodeChanges,
                            request.AgentName, request.ModelName,
                            status: ThreadMessageStatus.Streaming));
                    var pendingCalls = ImmutableDictionary<string, FunctionCallContent>.Empty;
                    string? lastCallKey = null;

                    // Diagnostic: log the message + tool set we hand to the chat client.
                    // The 6 OrleansDelegation* tests fail with toolCalls=0 — this lets us
                    // see whether the test's fake client sees delegate_to_agent in
                    // options.Tools (which gates its FunctionCallContent emission).
                    logger.LogInformation(
                        "[ThreadExec] STREAM_BEGIN threadPath={ThreadPath} agent={Agent} model={Model} msgs={Msgs}",
                        threadPath, request.AgentName ?? "(default)", request.ModelName ?? "(default)",
                        allMessages.Count);

                    // Pass ALL messages through the official AgentChatClient path
                    await foreach (var update in client.GetStreamingResponseAsync(allMessages, ct))
            {
                // Diagnostic: surface every content-kind we see. If FunctionInvokingChatClient
                // eats the FunctionCallContent before we see it, this loop only logs TextContent /
                // UsageContent — the smoking gun for "toolCalls=0" failures.
                if (update.Contents.Count > 0)
                {
                    logger.LogDebug("[ThreadExec] STREAM_UPDATE kinds=[{Kinds}]",
                        string.Join(",", update.Contents.Select(c => c.GetType().Name)));
                }

                // Capture function call / delegation activity for execution status
                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent functionCall)
                    {
                        logger.LogDebug("[ThreadExec] TOOL_START: {Name} callId={CallId} args={Args}",
                            functionCall.Name, functionCall.CallId,
                            SerializeArgs(functionCall.Arguments)?[..Math.Min(100, SerializeArgs(functionCall.Arguments)?.Length ?? 0)]);
                        var formatted = ToolStatusFormatter.Format(functionCall);
                        var argsDetail = SerializeArgs(functionCall.Arguments);
                        currentStatus = argsDetail != null
                            ? $"{formatted}\n{argsDetail}"
                            : formatted;

                        var callKey = functionCall.CallId ?? $"{functionCall.Name}_{pendingCalls.Count}";
                        var isDuplicate = pendingCalls.ContainsKey(callKey);
                        pendingCalls = pendingCalls.SetItem(callKey, functionCall);
                        lastCallKey = callKey;

                        // Add pending tool call to local log — will be pushed on next throttled update.
                        // Dedupe by CallId across the entire conversation: FCC can re-emit the same
                        // FunctionCallContent in turn 2's output stream (history echo), and the
                        // CallId-keyed `pendingCalls` map gets cleared on FunctionResultContent,
                        // so the second emission isn't caught by `isDuplicate`. Checking the log
                        // itself by CallId also dedupes against ChatClientAgentFactory.ExecuteDelegationAsync's
                        // StampTerminal mirror, which writes the same CallId at terminal.
                        lock (logLock)
                        {
                            var callId = functionCall.CallId;
                            var alreadyByCallId = callId is not null
                                && toolCallLog.Any(e => e.CallId == callId);
                            var alreadyPending = toolCallLog.Any(e => e.Name == functionCall.Name && e.Result == null);
                            if (!isDuplicate && !alreadyPending && !alreadyByCallId)
                            {
                                // Drain a pending Dispatched path if Dispatched fired
                                // before this FunctionCallContent reached the loop —
                                // the bare entry would otherwise ship without a
                                // DelegationPath (DelegationWriteCountTest failure
                                // mode). Drain only for delegate_to* names.
                                string? stampedPath = null;
                                if (functionCall.Name.StartsWith("delegate_to") && !pendingDispatchedPaths.IsEmpty)
                                {
                                    pendingDispatchedPaths = pendingDispatchedPaths.Dequeue(out stampedPath);
                                    logger.LogInformation("[ThreadExec] DELEGATION_DRAINED_STAMP: name={Name} delPath={DelPath} callId={CallId}",
                                        functionCall.Name, stampedPath, callId);
                                }
                                toolCallLog = toolCallLog.Add(new ToolCallEntry
                                {
                                    Name = functionCall.Name,
                                    DisplayName = formatted,
                                    Arguments = argsDetail,
                                    CallId = callId,
                                    DelegationPath = stampedPath,
                                    Timestamp = DateTime.UtcNow
                                });
                            }
                        }
                    }
                    else if (content is UsageContent usage)
                    {
                        // Aggregate token usage across stream chunks. Providers vary —
                        // some report once at the end, others on every chunk; sum either way.
                        var d = usage.Details;
                        if (d?.InputTokenCount is { } it)
                            inputTokens = (inputTokens ?? 0) + (int)it;
                        if (d?.OutputTokenCount is { } ot)
                            outputTokens = (outputTokens ?? 0) + (int)ot;
                        if (d?.TotalTokenCount is { } tt)
                            totalTokens = (totalTokens ?? 0) + (int)tt;
                    }
                    else if (content is FunctionResultContent functionResult)
                    {
                        logger.LogDebug("[ThreadExec] TOOL_RESULT: {Time:HH:mm:ss.fff} callId={CallId}, success={Success}, resultLen={Length}",
                            DateTime.UtcNow, functionResult.CallId,
                            functionResult.Result?.ToString()?.StartsWith("Error") != true,
                            functionResult.Result?.ToString()?.Length ?? 0);
                        // Match result to pending call — try CallId first, then last pending call
                        var resultKey = functionResult.CallId ?? lastCallKey;
                        FunctionCallContent? originalCall = null;
                        if (resultKey != null && pendingCalls.TryGetValue(resultKey, out originalCall))
                            pendingCalls = pendingCalls.Remove(resultKey);
                        originalCall ??= pendingCalls.Values.LastOrDefault();

                        if (originalCall != null)
                        {
                            string? delegationPath = null;
                            string? resultText = null;
                            bool isSuccess;

                            // Extract typed result fields when available (DelegationResult, etc.)
                            var (extractedText, extractedPath, extractedSuccess) = ExtractToolResult(functionResult.Result);
                            resultText = extractedText;
                            isSuccess = extractedSuccess;

                            if (originalCall.Name.StartsWith("delegate_to"))
                                delegationPath = extractedPath;

                            // Replace pending entry with final (has Result + DelegationPath).
                            // Preserve DelegationPath if already stamped by UpdateDelegationStatus.
                            // FindIndex + SetItem must be atomic — without the lock a concurrent
                            // Select/.ToImmutableList rebuild from another path (UpdateDelegationStatus
                            // or middleware ForwardToolCall) can change the list reference between
                            // FindIndex returning idx and SetItem(idx) consuming it.
                            // Repro: OrleansDelegationTest's
                            // `Index was out of range. (Parameter 'index')`.
                            lock (logLock)
                            {
                                // Match priority:
                                //  1) By DelegationPath when we have one — covers the case where
                                //     ChatClientAgentFactory.ExecuteDelegationAsync's StampTerminal
                                //     already populated Result on the matching delegation entry
                                //     (so the name+Result-null check below misses).
                                //  2) Fall back to name + null Result for the standard
                                //     bare-then-result flow.
                                var idx = -1;
                                if (delegationPath is not null)
                                    idx = toolCallLog.FindIndex(e => e.DelegationPath == delegationPath);
                                if (idx < 0)
                                    idx = toolCallLog.FindIndex(e => e.Name == originalCall.Name && e.Result == null);
                                var existingDelegationPath = idx >= 0 ? toolCallLog[idx].DelegationPath : null;
                                logger.LogDebug(
                                    "[ThreadExec] TOOL_RESULT_REPLACE: name={Name} callId={CallId} idx={Idx} " +
                                    "existingDelegationPath={ExistingDelegationPath} extractedPath={ExtractedPath} logSize={LogSize}",
                                    originalCall.Name, originalCall.CallId, idx,
                                    existingDelegationPath ?? "(null)", delegationPath ?? "(null)", toolCallLog.Count);
                                var finalEntry = new ToolCallEntry
                                {
                                    Name = originalCall.Name,
                                    DisplayName = ToolStatusFormatter.Format(originalCall),
                                    Arguments = SerializeArgs(originalCall.Arguments),
                                    Result = Truncate(resultText),
                                    IsSuccess = isSuccess,
                                    DelegationPath = delegationPath ?? existingDelegationPath,
                                    CallId = originalCall.CallId,
                                    Timestamp = DateTime.UtcNow
                                };
                                toolCallLog = idx >= 0 ? toolCallLog.SetItem(idx, finalEntry) : toolCallLog.Add(finalEntry);
                                logger.LogDebug("[ThreadExec] TOOL_DONE: {Time:HH:mm:ss.fff} {Name} callId={CallId} delegation={Delegation} resultLen={ResultLen}",
                                    DateTime.UtcNow, originalCall.Name, originalCall.CallId, delegationPath,
                                    finalEntry.Result?.Length ?? 0);
                            }
                        }
                        currentStatus = null; // Tool call completed
                    }
                }

                // Stamp delegation paths on any unmatched delegation tool calls.
                // Same lock as every other toolCallLog mutation site — otherwise this
                // rebuild silently overwrites an in-flight FunctionResultContent
                // SetItem and the completed entry's Result/IsSuccess fields are lost.
                // Also guards responseText.Append + ToString from the StringBuilder
                // chunk-walk race with UpdateDelegationStatus on the sub-thread.
                ImmutableList<ToolCallEntry> snapshotLog;
                ImmutableList<NodeChangeEntry> snapshotNodes;
                string textSnapshot;
                lock (logLock)
                {
                    if (!string.IsNullOrEmpty(update.Text))
                        responseText.Append(update.Text);

                    // ActiveDelegationPaths is maintained by AgentChatClient.EmitDelegationEvent
                    // (Dispatched adds, Terminal removes). Order is non-deterministic for an
                    // unordered set; the assumption that pathValues[idx] aligns with the
                    // i-th unmatched delegate_to entry holds only because each Dispatched
                    // event also fires the same stamp via the Delegations subscription
                    // installed below — this fallback covers the streaming-loop edge case
                    // where a delegation lands between Dispatched and the next streaming
                    // emission, but the subscription is the authoritative stamper.
                    var pathValues = chatClient.ActiveDelegationPaths.ToList();
                    var pathIdx = 0;
                    toolCallLog = toolCallLog.Select(e =>
                        e.Name.StartsWith("delegate_to") && e.DelegationPath == null && pathIdx < pathValues.Count
                            ? e with { DelegationPath = pathValues[pathIdx++] }
                            : e).ToImmutableList();
                    snapshotLog = toolCallLog;
                    snapshotNodes = nodeChangeLog;
                    textSnapshot = responseText.ToString();
                }

                snapshots.OnNext(new StreamingSnapshot(
                    textSnapshot, snapshotLog, snapshotNodes));
            }

                    snapshots.OnCompleted();
                    // Capture a final consistent snapshot under the same lock that
                    // guarded every prior Append/ToString — UpdateDelegationStatus
                    // can still fire after the await foreach exits if a sub-thread
                    // completes during the trailing iteration.
                    string finalText;
                    int finalTextLen;
                    ImmutableList<ToolCallEntry> finalToolCalls;
                    ImmutableList<NodeChangeEntry> finalNodeChanges;
                    lock (logLock)
                    {
                        finalText = responseText.ToString();
                        finalTextLen = responseText.Length;
                        finalToolCalls = toolCallLog;
                        finalNodeChanges = nodeChangeLog;
                    }
                    // include token usage + completion timestamp so the cell can show duration / tokens.
                    var aggregatedChanges = AggregateNodeChanges(finalNodeChanges);
                    if (totalTokens is null && (inputTokens.HasValue || outputTokens.HasValue))
                        totalTokens = (inputTokens ?? 0) + (outputTokens ?? 0);
                    logger.LogInformation("[ThreadExec] EXECUTION_COMPLETE: {Time:HH:mm:ss.fff} threadPath={ThreadPath}, responseLength={Length}, toolCalls={ToolCalls}, tokens={In}/{Out}/{Total}",
                        DateTime.UtcNow, threadPath, finalTextLen, finalToolCalls.Count,
                        inputTokens, outputTokens, totalTokens);
                    // Empty stream + no tool calls = silent agent failure
                    // (e.g. underlying API returned nothing). Surface so the
                    // user sees a real terminal state instead of a blank cell.
                    if (string.IsNullOrEmpty(finalText) && finalToolCalls.IsEmpty)
                        finalText = "*Agent returned no response — streaming completed with zero tokens.*";

                    // Dedicated summary: parse <summary>...</summary> the agent
                    // is instructed to emit at end-of-response (system prompt
                    // boilerplate). If present, that inner text is the tool-
                    // call result returned to a delegating parent; the marker
                    // block is also stripped from finalText so the user sees a
                    // clean response. If the marker is absent (agent forgot, or
                    // an external chat client), summaryText falls back to
                    // finalText. No extra LLM round-trip — same single
                    // streaming foreach drives both. Streaming-time pushes
                    // already strip the in-flight <summary> block via
                    // StripSummaryBlock so the user never sees the markers.
                    var summaryText = finalText;
                    var summaryMatch = System.Text.RegularExpressions.Regex.Match(
                        finalText,
                        @"<summary>(?<inner>[\s\S]*?)</summary>",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (summaryMatch.Success)
                    {
                        summaryText = summaryMatch.Groups["inner"].Value.Trim();
                        finalText = (finalText[..summaryMatch.Index] + finalText[(summaryMatch.Index + summaryMatch.Length)..]).TrimEnd();
                        finalTextLen = finalText.Length;
                    }

                    // 🚨 Subscribe to actually fire the cold cache.Update write.
                    // Single push: writes Text=finalText, Summary=summaryText,
                    // Status=Completed atomically to the response cell.
                    PushToResponseMessage(finalText, finalToolCalls, aggregatedChanges,
                        request.AgentName, request.ModelName,
                        inputTokens: inputTokens, outputTokens: outputTokens,
                        totalTokens: totalTokens, completedAt: DateTime.UtcNow,
                        status: ThreadMessageStatus.Completed,
                        summary: summaryText).Subscribe(
                        _ => { },
                        ex => execLogger?.LogWarning(ex,
                            "PushToResponseMessage(Completed) failed for {ThreadPath}", threadPath));
                    // Clear streaming state AND publish the dedicated Summary
                    // in the SAME stream.Update cycle as the Status → Idle
                    // flip. Single emission → the parent's reactive subscriber
                    // (DelegationTool) sees both Summary and Idle atomically,
                    // never reads a stale empty Summary in an interleaving.
                    UpdateThreadExecution(t => t with
                    {
                        Status = ThreadExecutionStatus.Idle, ExecutionStatus = null, ActiveMessageId = null,
                        ExecutionStartedAt = null, StreamingText = null, StreamingToolCalls = null,
                        PendingUserMessage = null, PendingAgentName = null, PendingModelName = null,
                        PendingContextPath = null, PendingAttachments = null,
                        Summary = summaryText
                    }).Subscribe(
                        _ => { },
                        ex => execLogger?.LogWarning(ex,
                            "UpdateThreadExecution(Idle/Completed): stream.Update failed for {ThreadPath}",
                            threadPath));
                    // Notify parent via SubmitMessageResponse so delegation callback resolves.
                    // Must post on the _Exec hub (hub) — the SubmitMessageResponse handler
                    // is registered there and forwards to the thread hub via ResponseFor.
                    NotifyParentCompletion(parentHub, threadPath, finalText, true, aggregatedChanges);
                    EmitCompletionNotification(parentHub, threadPath, finalText, request.AgentName);
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogInformation("[ThreadExec] CANCELLED: {Time:HH:mm:ss.fff} threadPath={ThreadPath}", DateTime.UtcNow, threadPath);
                        // ToString must be under logLock — UpdateDelegationStatus
                        // (sub-thread callback) can still Append concurrently after
                        // the try body exits.
                        string cancelText;
                        ImmutableList<ToolCallEntry> cancelToolCalls;
                        ImmutableList<NodeChangeEntry> cancelNodeChanges;
                        lock (logLock)
                        {
                            cancelText = responseText.ToString();
                            cancelToolCalls = toolCallLog;
                            cancelNodeChanges = nodeChangeLog;
                        }
                        // 🚨 Subscribe to fire the cold cache.Update — same reason as
                        // the Completed branch above.
                        PushToResponseMessage(cancelText, cancelToolCalls, cancelNodeChanges,
                            request.AgentName, request.ModelName,
                            completedAt: DateTime.UtcNow,
                            status: ThreadMessageStatus.Cancelled).Subscribe(
                            _ => { },
                            ex => execLogger?.LogWarning(ex,
                                "PushToResponseMessage(Cancelled) failed for {ThreadPath}", threadPath));
                        // Summary invariant: every Idle write must carry a Summary.
                        // Cancelled path has no agent-emitted <summary> block, so
                        // Summary defaults to the cancellation context's accumulated
                        // text — same as the user-visible response cell Text.
                        var cancelSummary = string.IsNullOrEmpty(cancelText)
                            ? "Cancelled before completion."
                            : cancelText;
                        UpdateThreadExecution(t => t with
                        {
                            Status = ThreadExecutionStatus.Idle, ExecutionStatus = null, ActiveMessageId = null,
                            ExecutionStartedAt = null, StreamingText = null, StreamingToolCalls = null,
                            Summary = cancelSummary
                        }).Subscribe(
                            _ => { },
                            ex => execLogger?.LogWarning(ex,
                                "UpdateThreadExecution(Idle/Cancelled): stream.Update failed for {ThreadPath}",
                                threadPath));
                        NotifyParentCompletion(parentHub, threadPath, cancelText, false, cancelNodeChanges);
                        EmitCompletionNotification(parentHub, threadPath, "Cancelled", request.AgentName);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "[ThreadExec] ERROR: {Time:HH:mm:ss.fff} threadPath={ThreadPath}", DateTime.UtcNow, threadPath);
                        // Same lock-guarded snapshot as the cancellation path.
                        string errorTextBase;
                        ImmutableList<ToolCallEntry> errorToolCalls;
                        ImmutableList<NodeChangeEntry> errorNodeChanges;
                        lock (logLock)
                        {
                            errorTextBase = responseText.ToString();
                            errorToolCalls = toolCallLog;
                            errorNodeChanges = nodeChangeLog;
                        }
                        var errorText = (errorTextBase + $"\n\n*Error: {ex.Message}*").Trim();
                        // 🚨 NO await on hub-touching observables in src/. Subscribe-
                        // continuation: push the error cell, then flip Idle, then notify.
                        // (Previous `.ToTask()` bridge would deadlock the action block —
                        // forbidden per feedback_no_totask_in_src.md / AsynchronousCalls.md.)
                        var pushErrorObs = PushToResponseMessage(errorText, errorToolCalls, errorNodeChanges,
                            request.AgentName, request.ModelName,
                            completedAt: DateTime.UtcNow,
                            status: ThreadMessageStatus.Error)
                            .Timeout(TimeSpan.FromSeconds(10));
                        var errorTextLocal = errorText;
                        var errorNodeChangesLocal = errorNodeChanges;
                        pushErrorObs.Subscribe(
                            _ => { },
                            pushEx => execLogger?.LogWarning(pushEx,
                                "PushToResponseMessage(Error) failed for {ThreadPath}", threadPath),
                            () =>
                            {
                                // Summary invariant for the Error path — non-empty.
                                var errorSummary = string.IsNullOrEmpty(errorTextLocal)
                                    ? $"Error: {ex.Message}"
                                    : errorTextLocal;
                                UpdateThreadExecution(t => t with
                                {
                                    Status = ThreadExecutionStatus.Idle, ExecutionStatus = null, ActiveMessageId = null,
                                    ExecutionStartedAt = null, StreamingText = null, StreamingToolCalls = null,
                                    Summary = errorSummary
                                }).Subscribe(
                                    _ => { },
                                    updEx => execLogger?.LogWarning(updEx,
                                        "UpdateThreadExecution(Idle/Error): stream.Update failed for {ThreadPath}",
                                        threadPath),
                                    () =>
                                    {
                                        NotifyParentCompletion(parentHub, threadPath, errorTextLocal, false, errorNodeChangesLocal);
                                        EmitCompletionNotification(parentHub, threadPath, errorTextLocal, request.AgentName);
                                    });
                            });
                    }
                    finally
                    {
                        delegationStampSub?.Dispose();
                        parentHub.Set<CancellationTokenSource>(null!);
                        executionCts.Dispose();
                        // No per-_Exec stream handle to dispose — writes went through
                        // IMeshNodeStreamCache.Update, whose upstream handle is owned
                        // by the cache and outlives this round.
                    }
                });
                    }); // end of LoadFullConversationHistory.Subscribe
                }); // end of contextNodeObs.Subscribe
                }, // end of WhenInitialized.Subscribe onNext
                ex =>
                {
                    // 🚨 Agent-init stalled or errored — surface and unstick the UI.
                    // Without this, IsExecuting stays true forever and the user sees
                    // a perpetually-"executing" thread (prod symptom 2026-05-20).
                    // Flips Status → Idle, clears ActiveMessageId, marks the response
                    // cell as Error, and notifies parent (delegation tool watchdog
                    // already handles the sub-thread side via the cancel
                    // propagation in ChatClientAgentFactory.ExecuteDelegationAsync).
                    logger.LogError(ex,
                        "[ThreadExec] Initialize failed / stalled for {ThreadPath} — flipping thread to Idle",
                        threadPath);

                    parentHub.GetWorkspace().GetMeshNodeStream().Update(node =>
                    {
                        if (node?.Content is not MeshThread t) return node!;
                        return node with
                        {
                            LastModified = DateTime.UtcNow,
                            Content = t with
                            {
                                Status = ThreadExecutionStatus.Idle,
                                ExecutionStatus = null,
                                ActiveMessageId = null,
                                ExecutionStartedAt = null,
                                StreamingText = null,
                                StreamingToolCalls = null
                            }
                        };
                    }).Subscribe(_ => { }, ex2 => logger.LogWarning(ex2,
                        "[ThreadExec] Init-stall unstick: stream.Update failed for {ThreadPath}",
                        threadPath));

                    // If the in-flight round has a response cell, stamp it with
                    // the error so the bubble shows something instead of an empty
                    // "Allocating agent..." placeholder.
                    var responsePath = $"{threadPath}/{responseMsgId}";
                    UpdateResponseCell(cache, responsePath, threadPath, responseMsgId,
                        mainEntity: threadPath,
                        msg => msg with
                        {
                            Text = (msg.Text ?? string.Empty) +
                                $"\n\n*Agent initialization stalled: {ex.Message}*",
                            Status = ThreadMessageStatus.Error,
                            CompletedAt = DateTime.UtcNow
                        },
                        logger);

                    NotifyParentCompletion(parentHub, threadPath,
                        $"Agent initialization stalled: {ex.Message}", success: false);
                });
        }); // end of clientObs.Subscribe

        // Register subscription for disposal — use parentHub's workspace
        // (this is the thread hub's own workspace, the natural lifetime owner).
        parentHub.GetWorkspace().AddDisposable(initSub);
    }

    /// <summary>
    /// Logging-only completion stub. The SubmitMessageResponse callback shape
    /// was deleted 2026-05-25; parent threads now observe sub-thread completion
    /// via the response cell's stream (Status flips to Completed/Cancelled/Error
    /// via PushToResponseMessage). Kept as a method for the existing 4 callsites
    /// — the delegation tool's reactive completion observation will replace the
    /// callsites in the next refactor pass.
    /// </summary>
    private static void NotifyParentCompletion(
        IMessageHub hub, string threadPath, string responseText, bool success,
        ImmutableList<NodeChangeEntry>? updatedNodes = null)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<AgentChatClient>>();
        logger.LogInformation(
            "[ThreadExec] NOTIFY_PARENT: threadPath={ThreadPath}, success={Success}, textLen={TextLen}, updatedNodes={UpdatedNodes}",
            threadPath, success, responseText.Length, updatedNodes?.Count ?? 0);
    }

    /// <summary>
    /// Posts a <see cref="Notification"/> satellite under the thread on
    /// successful round completion. The notification stores in the
    /// <c>notifications</c> table (satellite routing via
    /// <see cref="PartitionDefinition.StandardTableMappings"/>) and shows up
    /// in the user's bell — clicking it navigates to the thread.
    /// Fire-and-forget; failures are logged but don't fail the round.
    /// </summary>
    private static void EmitCompletionNotification(
        IMessageHub hub, string threadPath, string responseText, string? agentName)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.ThreadExecution");
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService == null)
        {
            logger?.LogDebug("[ThreadExec] EmitCompletionNotification: no IMeshService — skipping");
            return;
        }

        var threadName = (hub.GetWorkspace().GetStream(new MeshNodeReference())
            as ISynchronizationStream<MeshNode>)?.Current?.Value?.Name ?? "Thread";
        var preview = Truncate(responseText, 120) ?? "";

        NotificationService.CreateNotification(
            meshService,
            mainNodePath: threadPath,
            title: $"\"{threadName}\" is ready",
            message: preview,
            type: NotificationType.General,
            targetNodePath: threadPath,
            createdBy: agentName ?? "agent",
            icon: "/static/NodeTypeIcons/chat.svg")
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "[ThreadExec] Failed to create completion notification for {ThreadPath}",
                    threadPath));
    }

    /// <summary>
    /// Strips an in-flight or completed <c>&lt;summary&gt;...&lt;/summary&gt;</c>
    /// block from the agent's response so the user never sees the marker
    /// tags. Removes ANY closed block, then trims a trailing open <c>&lt;summary&gt;</c>
    /// (and everything after) so chunks mid-stream don't leak the partial
    /// inner text into the visible Text. Always returns a trimmed string.
    /// </summary>
    private static string StripSummaryBlock(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            text, @"<summary>[\s\S]*?</summary>", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var openIdx = stripped.LastIndexOf("<summary>", StringComparison.OrdinalIgnoreCase);
        if (openIdx >= 0)
            stripped = stripped[..openIdx];
        return stripped.TrimEnd();
    }

    /// <summary>
    /// Aggregates node change entries: for the same path, takes min(VersionBefore) and max(VersionAfter).
    /// This merges changes from the current thread and any delegation sub-threads.
    /// </summary>
    internal static ImmutableList<NodeChangeEntry> AggregateNodeChanges(ImmutableList<NodeChangeEntry> entries)
    {
        if (entries.Count <= 1) return entries;
        return entries
            .GroupBy(e => e.Path)
            .Select(g => g.Aggregate((a, b) => a with
            {
                VersionBefore = Min(a.VersionBefore, b.VersionBefore),
                VersionAfter = Max(a.VersionAfter, b.VersionAfter),
                Operation = b.Operation // Last operation wins (e.g., Created then Updated → Updated)
            }))
            .ToImmutableList();

        static long? Min(long? a, long? b) => a == null ? b : b == null ? a : Math.Min(a.Value, b.Value);
        static long? Max(long? a, long? b) => a == null ? b : b == null ? a : Math.Max(a.Value, b.Value);
    }

    private static string? SerializeArgs(IDictionary<string, object?>? args)
    {
        if (args == null || args.Count == 0)
            return null;
        try
        {
            // Format as readable key=value pairs instead of raw JSON
            var parts = ImmutableList<string>.Empty;
            foreach (var (key, value) in args)
            {
                var valStr = value switch
                {
                    null => "null",
                    JsonElement je => je.ValueKind == JsonValueKind.String
                        ? je.GetString() ?? ""
                        : je.ToString(),
                    _ => value.ToString() ?? ""
                };
                // Truncate long values and unescape unicode
                if (valStr.Length > 200)
                    valStr = valStr[..197] + "...";
                parts = parts.Add($"{key}: {valStr}");
            }
            return string.Join("\n", parts);
        }
        catch
        {
            return null;
        }
    }

    private static CancellationTokenSource StoreNewCts(IMessageHub hub)
    {
        var cts = new CancellationTokenSource();
        hub.Set(cts);
        return cts;
    }

    private static string? Truncate(string? value, int maxLength = 500)
    {
        if (value == null || value.Length <= maxLength)
            return value;
        return value[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// Extracts result text, delegation path, and success from a tool result.
    /// Handles DelegationResult objects directly (no ToString → JSON round-trip).
    /// Falls back to JSON parsing for serialized results, then plain toString.
    /// </summary>
    /// <summary>
    /// Merge the in-memory <c>toolCallLog</c> with the cell's current persisted
    /// <c>ToolCalls</c>. Both sources can write the same logical entry:
    /// <list type="bullet">
    /// <item>Streaming loop appends bare entries (Result=null) on FCC FunctionCallContent.</item>
    /// <item><see cref="ChatClientAgentFactory.ExecuteDelegationAsync"/> writes a
    ///   terminal entry (Result+Status+DelegationPath) through cache.Update.</item>
    /// </list>
    /// Without merge, the streaming loop's final write would CLOBBER the cache's
    /// terminal stamp. Pair entries by <see cref="ToolCallEntry.DelegationPath"/>
    /// when both have one; else by <see cref="ToolCallEntry.Name"/>+positional
    /// match. Prefer whichever has <see cref="ToolCallEntry.Result"/> set
    /// (terminal beats in-flight). Order: follow toolCallLog (in-stream order).
    /// </summary>
    private static ImmutableList<ToolCallEntry> MergeToolCallEntries(
        ImmutableList<ToolCallEntry> current, ImmutableList<ToolCallEntry> incoming)
    {
        if (current.IsEmpty) return incoming;
        if (incoming.IsEmpty) return current;
        var consumedCurrent = new bool[current.Count];
        var builder = ImmutableList.CreateBuilder<ToolCallEntry>();
        foreach (var inc in incoming)
        {
            var idx = -1;
            if (inc.DelegationPath is { } dp)
            {
                for (var i = 0; i < current.Count; i++)
                {
                    if (!consumedCurrent[i] && current[i].DelegationPath == dp)
                    { idx = i; break; }
                }
            }
            if (idx < 0)
            {
                for (var i = 0; i < current.Count; i++)
                {
                    if (!consumedCurrent[i] && current[i].Name == inc.Name && current[i].Result != null && inc.Result == null)
                    { idx = i; break; }
                }
            }
            if (idx >= 0)
            {
                consumedCurrent[idx] = true;
                var cur = current[idx];
                // Prefer the side that's "further along" — Result populated +
                // terminal Status. Field-by-field: keep cur.Result when inc.Result
                // is null; keep cur.Status when it's terminal and inc is Streaming.
                var preferred = inc.Result is null && cur.Result is not null ? cur : inc;
                builder.Add(preferred with
                {
                    DelegationPath = preferred.DelegationPath ?? cur.DelegationPath ?? inc.DelegationPath,
                    Result = preferred.Result ?? cur.Result ?? inc.Result,
                    Status = cur.Status != ToolCallStatus.Streaming && inc.Status == ToolCallStatus.Streaming
                        ? cur.Status : preferred.Status
                });
            }
            else
            {
                builder.Add(inc);
            }
        }
        // Append any cell-only entries that incoming didn't carry (e.g. the
        // terminal stamp may have landed but the streaming loop's snapshot was
        // taken before the next FCC chunk re-appended the bare entry).
        for (var i = 0; i < current.Count; i++)
        {
            if (!consumedCurrent[i] && current[i].DelegationPath is not null)
                builder.Add(current[i]);
        }
        return builder.ToImmutable();
    }

    private static (string? ResultText, string? DelegationPath, bool IsSuccess) ExtractToolResult(object? result)
    {
        if (result is null)
            return (null, null, true);

        // Typed DelegationResult — direct property access, no parsing
        if (result is DelegationResult dr)
            return (dr.Result, dr.ThreadId, dr.Success);

        var text = result.ToString();
        if (string.IsNullOrEmpty(text))
            return (null, null, true);

        // Try JSON parsing only if text looks like a JSON object — arrays/scalars don't carry
        // threadId/result/success, and TryGetProperty would throw InvalidOperationException on them.
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.Length > 0 && trimmed[0] == '{')
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (text, null, !text.StartsWith("Error", StringComparison.Ordinal));

            string? threadId = null;
            if (root.TryGetProperty("threadId", out var tidProp) ||
                root.TryGetProperty("ThreadId", out tidProp))
                threadId = tidProp.GetString();

            string? resultText = null;
            if (root.TryGetProperty("result", out var resProp) ||
                root.TryGetProperty("Result", out resProp))
                resultText = resProp.GetString();

            var success = true;
            if (root.TryGetProperty("success", out var sucProp) ||
                root.TryGetProperty("Success", out sucProp))
                success = sucProp.GetBoolean();

            return (resultText ?? text, threadId, success);
        }
        catch
        {
            // Not JSON — use raw text
        }

        var isSuccess = !text.StartsWith("Error", StringComparison.Ordinal);
        return (text, null, isSuccess);
    }

    /// <summary>
    /// Stream-update cancellation: clients flip <see cref="MeshThread.RequestedCancellationAt"/>
    /// on the thread node via <c>workspace.GetMeshNodeStream(threadPath).Update(...)</c>.
    /// The watcher below observes the OWN thread node, treats every transition
    /// to "<c>RequestedCancellationAt &gt; ExecutionStartedAt</c>" as a cancel
    /// signal, cancels the stored CTS, and propagates the same flip onto every
    /// active delegation sub-thread.
    ///
    /// <para>The bare <c>RequestedCancellationAt</c> compare is the only single-
    /// flight needed: the action block serialises Updates, and once we've
    /// cancelled we record <c>handledAt</c> in process memory so the same
    /// timestamp isn't acted on twice. A subsequent round starts when
    /// <c>ExecutionStartedAt</c> moves past <c>RequestedCancellationAt</c>;
    /// future flips beat that threshold and trigger again.</para>
    /// </summary>
    private static void InstallCancellationWatcher(IMessageHub hub)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        var threadPath = hub.Address.Path;
        DateTime? lastHandledAt = null;

        var sub = hub.GetWorkspace().GetMeshNodeStream()
            .Where(n => n?.Content is MeshThread t
                && t.RequestedCancellationAt is { } req
                && (lastHandledAt is null || req > lastHandledAt.Value)
                // Only act when the request is fresher than the current round —
                // a stale flag from before this round's start is not a cancel.
                && (t.ExecutionStartedAt is null || req >= t.ExecutionStartedAt.Value))
            .Subscribe(
                node =>
                {
                    var thread = (MeshThread)node!.Content!;
                    lastHandledAt = thread.RequestedCancellationAt;

                    // Propagate to every active delegation sub-thread via the
                    // canonical IMeshNodeStreamCache. The sub-thread is a
                    // non-own path; routing through the cache keeps a single
                    // shared handle for every reader (the sub-thread's own
                    // cancel watcher) and avoids opening an ad-hoc remote
                    // stream that subsequent readers wouldn't see.
                    //
                    // 🚨 Discover sub-thread paths from TWO sources:
                    //  (a) thread.StreamingToolCalls — persisted via the
                    //      streaming-loop throttle. STALE when the loop is
                    //      blocked inside a delegate_to_agent call (which is
                    //      exactly when we most need to cancel).
                    //  (b) AgentChatClient.DelegationPaths — live in-memory
                    //      registry on the parent's chat client, written
                    //      synchronously by ExecuteDelegationAsync when each
                    //      sub-thread is dispatched. Always current.
                    // Union of both: never miss a hung sub-thread whose path
                    // hasn't yet been throttle-persisted into (a).
                    // (Repro: SubThreadHangRepro.HungSubThread_UserCancel*
                    // demonstrated (a) alone fails to settle the sub-thread.)
                    var subPaths = ImmutableHashSet<string>.Empty;
                    if (thread.StreamingToolCalls is { Count: > 0 })
                    {
                        foreach (var tc in thread.StreamingToolCalls.Where(
                            tc => !string.IsNullOrEmpty(tc.DelegationPath) && tc.Result == null))
                            subPaths = subPaths.Add(tc.DelegationPath!);
                    }
                    var chat = hub.Get<AgentChatClient>();
                    if (chat is not null)
                    {
                        foreach (var subPath in chat.ActiveDelegationPaths)
                            if (!string.IsNullOrEmpty(subPath))
                                subPaths = subPaths.Add(subPath);
                    }

                    foreach (var subPath in subPaths)
                    {
                        logger?.LogInformation(
                            "[ThreadExec] Propagating cancel to sub-thread {SubThread}", subPath);
                        hub.GetWorkspace().GetMeshNodeStream(subPath).Update(
                            curr => curr?.Content is MeshThread sub
                                ? curr with { Content = sub with { RequestedCancellationAt = thread.RequestedCancellationAt } }
                                : curr!)
                            .Subscribe(_ => { }, ex => logger?.LogWarning(ex,
                                "[ThreadExec] Cancel propagation failed for {SubThread}", subPath));
                    }

                    // Cancel own execution via CancellationTokenSource (streaming
                    // runs on thread pool). The CTS was stored on the parent
                    // thread hub via Set — the _Exec hub stored it on its parent
                    // (= this hub).
                    //
                    // Defensive ObjectDisposedException catch: a race window
                    // exists where the CTS may already be disposed by the time
                    // we reach Cancel — the round's own finally block disposes
                    // it after Set null, and an emission from the workspace
                    // stream that crossed that boundary can land here with a
                    // stale reference. Swallowing the exception keeps the sync
                    // stream healthy (SetCurrent's warning would otherwise tear
                    // down unrelated observers via ReplaySubject failure modes).
                    // Repro:
                    // InboxToolIntegrationTest.Cancel_WithPendingMessages_DispatchesNextRoundAfterCleanup.
                    var cts = hub.Get<CancellationTokenSource>();
                    if (cts != null)
                    {
                        try
                        {
                            logger?.LogDebug("[ThreadExec] Cancelling own execution for {ThreadPath}", threadPath);
                            cts.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                            logger?.LogDebug(
                                "[ThreadExec] Cancel: CTS already disposed for {ThreadPath} — round already torn down",
                                threadPath);
                        }
                    }
                },
                ex => logger?.LogWarning(ex,
                    "[ThreadExec] Cancellation watcher faulted for {ThreadPath}", threadPath));

        hub.RegisterForDisposal(sub);
    }

    /// <summary>
    /// Loads ALL prior ThreadMessage cells (both user and assistant) for the
    /// thread, excluding the current submission's user cell and any cell with
    /// empty text (e.g. the just-created in-flight response cell). Ordered by
    /// timestamp. Used per-round to give the agent full conversation context —
    /// without this, every round only sees the new user message and tests like
    /// <c>ChatHistoryTest</c> see "I received 2 messages" forever.
    /// </summary>
    internal static IObservable<IReadOnlyList<ChatMessage>> LoadFullConversationHistoryFromMesh(
        IMessageHub hub, string threadPath, string? excludeUserMessageId, string? excludeResponseMessageId,
        ILogger logger, TimeSpan? cellTimeout = null)
    {
        var perCellTimeout = cellTimeout ?? TimeSpan.FromSeconds(5);
        // 🚨 Read thread + each cell from IMeshNodeStreamCache — the hot, shared,
        // path-keyed Replay(1) handle every consumer subscribes to. The same cache
        // that the per-node hub's writes flow through, so reads here observe the
        // exact post-write state without going through IMeshQueryCore (which lags).
        // Walk the thread's Messages property for the cell IDs (authoritative ordered
        // list of cells in this thread).
        var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        return cache.GetStream(threadPath)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .Select(threadNode => threadNode.Content as MeshThread)
            .Where(t => t != null)
            .SelectMany(thread =>
            {
                var cellIds = thread!.Messages
                    .Where(id => id != excludeUserMessageId && id != excludeResponseMessageId)
                    .ToList();
                if (cellIds.Count == 0)
                    return Observable.Return<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());

                // 🚨 Fan out cell reads in PARALLEL via CombineLatest — each
                // `cache.GetStream(path)` is its own hot Replay(1), so they all
                // subscribe / receive content concurrently. The serial `.Concat()`
                // shape was waiting up to N × budget when cells were cold.
                // Per-cell semantics: wait for content with text (cache may emit a
                // pre-text shell first), Take(1), then Timeout(perCellTimeout). On
                // per-cell failure → emit a sentinel null so CombineLatest still fires
                // — the projector filters nulls and the caller decides what to do
                // (warn + proceed with partial / throw if zero loaded).
                var cellLookups = cellIds
                    .Select(id =>
                        cache.GetStream($"{threadPath}/{id}")
                            .Where(n => n.Content is ThreadMessage m && !string.IsNullOrEmpty(m.Text))
                            .Take(1)
                            .Timeout(perCellTimeout)
                            .Select(n => (MeshNode?)n)
                            .Catch<MeshNode?, Exception>(ex =>
                            {
                                logger.LogWarning(ex,
                                    "[ThreadExec] HISTORY_CELL_DROP threadPath={ThreadPath} cellId={CellId} — cell unreadable within budget; will be omitted",
                                    threadPath, id);
                                return Observable.Return<MeshNode?>(null);
                            }))
                    .ToList();

                return Observable.CombineLatest(cellLookups)
                    .Take(1)
                    .Select(nodes =>
                    {
                        var messages = nodes
                            .Where(n => n is not null)
                            .Select(n => (ThreadMessage)n!.Content!)
                            .OrderBy(m => m.Timestamp)
                            .Select(m =>
                            {
                                var role = string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
                                    ? ChatRole.User
                                    : ChatRole.Assistant;
                                return new ChatMessage(role, m.Text);
                            })
                            .ToList();

                        // 🚨 Hard failure if every cell dropped despite expecting some:
                        // submitting an EMPTY history when the thread has prior turns
                        // would silently corrupt the agent's context (ChatHistoryTest
                        // would assert "5 messages" instead of "4"). Surface as a
                        // TimeoutException so the round fails loud instead of producing
                        // a misleading assertion downstream.
                        if (cellIds.Count > 0 && messages.Count == 0)
                            throw new TimeoutException(
                                $"LoadFullConversationHistoryFromMesh: expected {cellIds.Count} prior cells " +
                                $"for {threadPath} but ALL timed out / lacked text. Refusing to submit empty history.");

                        if (messages.Count < cellIds.Count)
                            logger.LogWarning(
                                "[ThreadExec] HISTORY_PARTIAL threadPath={ThreadPath} loaded={Loaded}/{Expected} — proceeding with partial history",
                                threadPath, messages.Count, cellIds.Count);

                        return (IReadOnlyList<ChatMessage>)messages;
                    });
            });
    }

    /// <summary>
    /// Loads prior user-message ThreadMessage cells for <paramref name="threadPath"/>
    /// by walking the live thread's <c>Messages</c> list and resolving each cell
    /// via <c>GetMeshNodeStream</c> (per-node hub) — the authoritative live read
    /// path. Filters to user-role cells, excludes <paramref name="excludeMessageId"/>
    /// (the current submission, whose text already comes via
    /// <c>request.UserMessageText</c>), and orders by timestamp. Called only on
    /// AgentChatClient cache miss (post-restart resume). The returned list is fed
    /// straight into the AgentChatClient constructor.
    /// </summary>
    private static IObservable<IReadOnlyList<ThreadMessage>> LoadPriorUserMessagesFromMesh(
        IMessageHub hub, string threadPath, string? excludeMessageId, ILogger<AgentChatClient> logger)
    {
        var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        return cache.GetStream(threadPath)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .Select(threadNode => threadNode.Content as MeshThread)
            .Where(t => t != null)
            .SelectMany(thread =>
            {
                var cellIds = thread!.Messages
                    .Where(id => excludeMessageId == null || id != excludeMessageId)
                    .ToList();
                if (cellIds.Count == 0)
                    return Observable.Return<IReadOnlyList<ThreadMessage>>(Array.Empty<ThreadMessage>());

                // 🚨 Subscribe-all-upfront via Observable.CombineLatest — N
                // per-cell synchronization streams subscribe simultaneously when
                // the consumer subscribes, so the N hub activations are
                // concurrent (≈ max(t_i)) instead of serial Σ(t_i) via .Concat().
                // Catch returns sentinel null so a single slow cell doesn't strand
                // the load. See AsynchronousCalls.md → "Subscribe-all-upfront cell loading".
                var cellLookups = cellIds.Select(id =>
                    cache.GetStream($"{threadPath}/{id}")
                        .Take(1)
                        .Timeout(TimeSpan.FromSeconds(5))
                        .Select(n => (MeshNode?)n)
                        .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null)));

                return Observable.CombineLatest(cellLookups)
                    .Take(1)
                    .Select(nodes => (IReadOnlyList<ThreadMessage>)nodes
                        .Where(n => n != null)
                        .Select(n => n!.Content as ThreadMessage)
                        .Where(m => m != null && string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(m => m!.Timestamp)
                        .Cast<ThreadMessage>()
                        .ToList());
            })
            .Catch<IReadOnlyList<ThreadMessage>, Exception>(ex =>
            {
                logger.LogWarning(ex,
                    "[ThreadExec] LoadPriorUserMessages: failed/timed out for {ThreadPath} — empty history",
                    threadPath);
                return Observable.Return<IReadOnlyList<ThreadMessage>>(Array.Empty<ThreadMessage>());
            });
    }
}
