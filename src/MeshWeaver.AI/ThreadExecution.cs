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
using MeshWeaver.Mesh.Threading;
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
internal static class ThreadExecution
{
    // Cell-stream patches are not free — at this cap a 30s response generates
    // ~300 patches instead of token-rate. 100ms is below the perceptual threshold
    // for "live typing" while keeping patch volume bounded.
    private static readonly TimeSpan StreamingSampleInterval = TimeSpan.FromMilliseconds(100);

    // #321: reasoning heartbeat. When the model produces no stream update for longer
    // than this (between tool calls, or while a tool call is in flight), the streaming
    // Sample(100ms) hot path stops emitting and the status line would otherwise freeze on
    // the last tool-call stamp — "thinking" indistinguishable from "stuck". A gap longer
    // than this threshold ticks a "Reasoning… (Ns)" heartbeat onto the thread's
    // ExecutionStatus. Also the tick period, so writes stay bounded (one per interval).
    private static readonly TimeSpan HeartbeatIdleThreshold = TimeSpan.FromSeconds(3);

    // #321: acknowledgement stamped onto MeshThread.ExecutionStatus the instant a Stop
    // (RequestedStatus = Cancelled) is observed while a round is executing, so the UI
    // confirms the Stop was registered rather than freezing on the previous tool-call
    // status while the in-flight tool call drains (up to its ~30s timeout).
    internal const string CancellationRequestedStatus =
        "Cancellation requested — stopping after current operation…";

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
    /// <c>IMeshNodeStreamCache.Update</c> — the same shared handle the
    /// GUI subscribers read from, so the patch is observed in order.
    /// </summary>
    internal static void UpdateResponseCell(
        IMessageHub hub,
        string responsePath,
        string threadPath,
        string responseMsgId,
        string mainEntity,
        Func<ThreadMessage, ThreadMessage> mutate,
        ILogger? logger)
    {
        hub.GetMeshNodeStream(responsePath).Update(node =>
        {
            var current = node.ContentAs<ThreadMessage>(hub.JsonSerializerOptions, logger);
            // Existing node whose content can't be recovered → leave it alone, NEVER clobber.
            if (node?.Content is not null && current is null)
                return node;
            current ??= new ThreadMessage
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

    internal static MessageHubConfiguration AddThreadExecution(this MessageHubConfiguration configuration)
        => configuration
            // No verb-shaped triggers — every thread state mutation rides
            // workspace.GetMeshNodeStream().Update(...) on a control-plane
            // field, observed by an owning-hub watcher. See
            // RequestViaStreamUpdate.md and ActivityControlPlane.md.
            //
            // Round dispatch (Idle → StartingExecution): InstallSubmissionWatcher
            // claims directly on the thread hub's action block. The _Exec
            // hosted hub (InstallExecutionHub) subscribes to the parent's
            // stream and continues with Step B + C on its own action block.
            //
            // Resubmit / delete-from / submission-failure are now done inline
            // by HubThreadExtensions — single stream.Update per operation, no
            // intent fields, no per-operation watchers.
            .WithHandler<MeshWeaver.AI.Delegation.HeartbeatTick>(
                MeshWeaver.AI.Delegation.DelegationHandlers.HandleHeartbeatTick)
            .WithHandler<MeshWeaver.AI.Delegation.CancelDelegationSubThread>(
                MeshWeaver.AI.Delegation.DelegationHandlers.HandleCancelDelegationSubThread)
            .WithInitialization(SetThreadHubIdentity)
            .WithInitialization(InitializeThreadLifecycle)
            .WithInitialization(InstallCancellationWatcher)
            .WithInitialization(InstallExecutionHub)
            .WithInitialization(InstallSubmissionWatcher)
            .WithInitialization(InstallHeartbeatTicker);

    /// <summary>
    /// Eagerly creates the <c>_Exec</c> hosted hub at thread hub init time and
    /// installs its round watcher. The watcher subscribes to the parent thread
    /// node's stream via the shared <see cref="IMeshNodeStreamCache"/>; on the
    /// first emission per claim with <c>Status == StartingExecution</c>
    /// (<see cref="System.Reactive.Linq.Observable.DistinctUntilChanged{TSource,TKey}(IObservable{TSource}, Func{TSource, TKey})"/>
    /// on <c>ExecutionStartedAt</c>), invokes
    /// <see cref="ThreadSubmissionServer.DispatchAfterClaim"/> to drain pending
    /// into Messages, allocate the response cell, transition to
    /// <c>Executing</c>, and start agent streaming.
    ///
    /// <para>Eager creation matters: the parent thread hub flips
    /// <c>Status → StartingExecution</c> as soon as the submission watcher
    /// claims; if <c>_Exec</c> isn't running yet, the resulting transition
    /// emission has no subscriber and the round stalls.</para>
    /// </summary>
    private static void InstallExecutionHub(IMessageHub threadHub)
    {
        threadHub.GetHostedHub(
            new Address($"{threadHub.Address}/_Exec"),
            config => config.WithInitialization(InstallExecRoundWatcher),
            HostedHubCreation.Always);
    }

    /// <summary>
    /// _Exec hosted hub's round watcher. Subscribes to the parent thread node's
    /// stream via the shared <see cref="IMeshNodeStreamCache"/> and fires
    /// <see cref="ThreadSubmissionServer.DispatchAfterClaim"/> for each
    /// <c>Idle → StartingExecution</c> transition. <c>DistinctUntilChanged</c>
    /// on <see cref="MeshThread.Status"/> (project first, then dedupe, then
    /// filter) ensures the watcher fires ONLY on the transition itself —
    /// other field updates (PendingUserMessages, Messages, etc.) that
    /// arrive while Status remains StartingExecution don't re-trigger dispatch.
    ///
    /// <para>Sets the round's <see cref="AccessContext"/> from
    /// <see cref="MeshThread.CreatedBy"/> before dispatching so downstream
    /// <c>CreateNodeRequest</c> calls (user cells, response cell) carry the
    /// right user identity.</para>
    /// </summary>
    private static void InstallExecRoundWatcher(IMessageHub execHub)
    {
        var parentHub = execHub.Configuration.ParentHub
            ?? throw new InvalidOperationException(
                "_Exec hosted hub has no ParentHub — cannot resolve thread path");
        var logger = execHub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        var threadPath = parentHub.Address.Path;

        var accessService = execHub.ServiceProvider.GetService<MeshWeaver.Messaging.AccessService>();

        // Self-healing: this watcher dispatches each StartingExecution round. If its
        // stream FAULTS it must not die silently — a dead watcher means a claimed
        // round never dispatches (Status stuck at StartingExecution, IsExecuting
        // forever): the live-path "observer dies" deadlock. On fault, re-establish.
        IDisposable? sub = null;
        var disposed = false;
        // 🚨 Observe the PARENT thread hub's AUTHORITATIVE own MeshNode stream —
        // NOT the cross-hub IMeshNodeStreamCache. The cache opens a remote
        // subscription to the owning grain and, on Orleans, replays/reorders:
        // it interleaves a STALE claim snapshot (StartingExecution, empty
        // PendingUserMessages) AFTER the committed Executing state. Because
        // DispatchAfterClaim plans the round from the emitted node, a stale
        // Pending=0 snapshot makes PlanNextRound return null → it ROLLS the
        // claim back to Idle, racing and reverting the in-flight commit. The
        // SubmissionWatcher re-claims, the cache replays stale again, and the
        // round live-locks (Resubmit_AfterExecution_DoesNotDeadlock hang).
        // parentHub.GetWorkspace().GetMeshNodeStream() is the same in-order,
        // typed-Content own stream the SubmissionWatcher reads, so the claim →
        // Executing transition is observed exactly once, in order, with the
        // real Pending — DistinctUntilChanged(Status) then fires the dispatch a
        // single time and never sees a phantom re-claim. (Using the THREAD
        // hub's workspace here, never the _Exec child's, also matches
        // feedback_synced_query_thread_hub.md.)
        void Establish() => sub = parentHub.GetWorkspace().GetMeshNodeStream()
            // Pair each emission with its current Status so DistinctUntilChanged
            // dedupes on the Status field only — concurrent field updates that
            // happen while Status stays StartingExecution must NOT re-fire.
            // ContentAs (deserialize), not `as`: the own stream is NOT guaranteed typed-Content —
            // when the polymorphic $type doesn't resolve it stays a JsonElement, and `as` → null
            // flip-flops Status StartingExecution↔null, defeating DistinctUntilChanged(Status) and
            // re-firing the claim (mitigated by the idempotent CAS, but still wrong).
            .Select(n => new { Node = n, Status = n.ContentAs<MeshThread>(parentHub.JsonSerializerOptions)?.Status })
            .DistinctUntilChanged(x => x.Status)
            .Where(x => x.Status == ThreadExecutionStatus.StartingExecution)
            .Select(x => x.Node)
            .Subscribe(
                node =>
                {
                    if (node?.Content is not MeshThread thread)
                    {
                        logger?.LogWarning(
                            "[ExecRoundWatcher] thread node has no MeshThread content for {ThreadPath}",
                            threadPath);
                        return;
                    }

                    // 🚨 Thread execution ALWAYS runs under the thread owner's
                    // identity. The cache stream's emission scheduler doesn't
                    // carry the originating user's AsyncLocal — without this
                    // scope, every read/write inside DispatchAfterClaim
                    // (drain pending, allocate response cell, stream LLM
                    // output) would be stamped with the cache identity, and
                    // the owning hub's RLS would deny. The access check that
                    // gated the dispatch already happened (the user with no
                    // access to the thread couldn't have flipped Status to
                    // StartingExecution).
                    using (MeshWeaver.Mesh.Security.AccessContextScope.FromNode(node, accessService, logger))
                    {
                        logger?.LogDebug(
                            "[ExecRoundWatcher] access context set: {User} for {ThreadPath}",
                            thread.CreatedBy ?? "(system fallback)", threadPath);

                        ThreadSubmissionServer.DispatchAfterClaim(parentHub, node, logger,
                            onFailure: () =>
                            {
                                logger?.LogWarning(
                                    "[ExecRoundWatcher] DispatchAfterClaim failed for {ThreadPath} — rolling Status back to Idle",
                                    threadPath);
                                parentHub.GetWorkspace().GetMeshNodeStream().Update(n =>
                                {
                                    var t = n.ContentAs<MeshThread>(parentHub.JsonSerializerOptions, logger);
                                    // Existing node whose content can't be recovered → leave it alone, NEVER clobber.
                                    if (n.Content is not null && t is null)
                                        return n;
                                    t ??= new MeshThread();
                                    return t.Status == ThreadExecutionStatus.StartingExecution
                                        ? n with { Content = t with { Status = ThreadExecutionStatus.Idle, ExecutionStartedAt = null } }
                                        : n;
                                }).Subscribe(
                                    _ => { },
                                    ex => logger?.LogWarning(ex,
                                        "[ExecRoundWatcher] rollback Update failed for {ThreadPath}", threadPath));
                            });
                    }
                },
                ex =>
                {
                    logger?.LogWarning(ex,
                        "[ExecRoundWatcher] stream errored for {ThreadPath} — re-establishing", threadPath);
                    if (!disposed)
                        System.Reactive.Linq.Observable.Timer(TimeSpan.FromSeconds(1))
                            .Subscribe(_ => Establish());
                });

        Establish();
        execHub.RegisterForDisposal(_ => { disposed = true; sub?.Dispose(); });
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
        // Read the OWN thread node's FIRST loaded emission off the local reducer - NO GetDataRequest.
        // A self-posted GetDataRequest on cold activation tripped the never-null AccessContext guard
        // ("hub=Thread, GetDataRequest, target=Thread", reproduced by `get @Thread`). Filter on MeshThread
        // content so we wait for the FULLY-LOADED node (with CreatedBy), exactly like
        // InitializeThreadLifecycle below; taking the first merely-non-null emission could read a
        // pre-content node, leave the owner unstamped, and wedge later writes (e.g. cancel not processed).
        hub.GetWorkspace().GetMeshNodeStream()
            .Where(n => n?.Content is MeshThread)
            .Take(1)
            .Subscribe(node =>
        {
            if (node is null) return;
            var createdBy = node.ContentAs<MeshThread>(hub.JsonSerializerOptions)?.CreatedBy;
            if (string.IsNullOrEmpty(createdBy))
                createdBy = node.CreatedBy;
            if (string.IsNullOrEmpty(createdBy))
                return;
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            var owner = new AccessContext { ObjectId = createdBy, Name = createdBy };
            // 🚨 OWNER-INJECTION: stamp the thread OWNER as BOTH the live Context AND the
            // CircuitContext. CircuitContext is the one that CARRIES FORWARD across Rx hops
            // (the deferred sync-write continuations where the AsyncLocal Context is wiped) —
            // SetContext alone was lost on the hop, so the owner-side data-source sync write
            // posted UpdateStreamRequest with a NULL AccessContext and the never-null guard
            // failed it closed (the cold-start submit deadlock: pending never landed, the
            // watcher saw pending=0 forever, no round dispatched). The thread owner is the
            // standing identity for EVERY operation on this thread hub. See OwnerInjection.md.
            accessService?.SetContext(owner);
            accessService?.SetCircuitContext(owner);
        });
    }

    /// <summary>
    /// Clean wake-up state machine. On hub activation, read the OWN node's FIRST
    /// stream emission (the loaded persisted state, correctly ordered on this
    /// hub's action block vs any subsequent writes) and drive any non-terminal
    /// state to a valid one exactly once. This replaces the old late
    /// <c>GetMeshNode</c> round-trip whose response could land AFTER later writes
    /// and clobber <c>Status → Idle</c> (the <c>check_inbox</c> phantom-drain
    /// flake).
    ///
    /// <para>Branches (after honoring any pending cancel first):
    /// <list type="bullet">
    /// <item><b>Executing</b> (with a response cell) → <b>resume</b> the same
    ///   cell by re-launching the streaming loop directly
    ///   (<see cref="ThreadSubmissionServer.ResumeInterruptedRound"/>) while
    ///   <c>Status</c> STAYS <c>Executing</c>. 🚨 Never re-enter
    ///   <c>StartingExecution</c> from <c>Executing</c> — that inverse of the
    ///   commit edge is the re-dispatch ping-pong.</item>
    /// <item><b>Executing</b> without a response cell → nothing to resume; reset
    ///   to <c>Idle</c> so the submission watcher can claim pending input.</item>
    /// <item><b>StartingExecution</b> without a response cell (no
    ///   <c>ActiveMessageId</c>) → an ORPHANED CLAIM (#539): the previous hub
    ///   incarnation wrote the claim but died before the <c>_Exec</c> commit, so
    ///   nothing re-drives it. CAS-guarded roll back to <c>Idle</c> so the
    ///   submission watcher re-claims the queued input as a fresh, live
    ///   transition the <c>_Exec</c> round watcher reliably observes. (WITH a
    ///   cell the commit already ran — no write.)</item>
    /// <item><b>Idle</b> / <b>Cancelled</b> (+ pending) → no write; the
    ///   submission watcher claims.</item>
    /// <item><b>Done</b> → terminal; leave it.</item>
    /// </list>
    /// Each child thread's own hub runs the same recovery recursively.</para>
    /// </summary>
    private static void InitializeThreadLifecycle(IMessageHub hub)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var workspace = hub.GetWorkspace();
        var accessService = hub.ServiceProvider.GetService<MeshWeaver.Messaging.AccessService>();
        var threadPath = hub.Address.Path;

        // Self-healing recovery. The prior one-shot Take(1).Timeout(15s) SILENTLY
        // GAVE UP if the loaded-state emission was missed or arrived late (the
        // dropped-patch subscribe-handshake race, amplified under load) — leaving a
        // stale Executing thread stuck forever. That is the sub-thread cold-load
        // "deadlock": not a lock, but a missed observation the recovery never
        // retried. We instead wait for the first real thread emission however long
        // it takes, and RE-ESTABLISH the observation if it faults before we read &
        // drive the loaded state — no observer may die before the thread reaches a
        // valid state. Driving an already-valid state is a no-op write (SetCurrent
        // skips equal), so re-establishing is cheap and idempotent.
        IDisposable? sub = null;
        // Terminal guard for the self-healing re-establish below. Set by the hub's
        // disposal hook (bottom of this method). Without it, a SYNCHRONOUS
        // Subscribe-time fault after teardown — the hub's Autofac scope is gone, so
        // GetMeshNodeStream().Subscribe() resolves a service off a disposed scope and
        // throws ObjectDisposedException straight out of Subscribe — would recurse
        // onError → Establish → onError until the stack overflows (the Orleans-shard
        // SIGABRT). The re-establish must stop when the hub is gone AND must hop off
        // the synchronous stack.
        var disposed = false;
        // Idempotency for the resume path: re-launch an interrupted round AT MOST
        // once per ActiveMessageId. The observation is self-healing (re-establishes
        // on fault), so without this a re-read of the same Executing state would
        // re-launch the streaming loop repeatedly. Captured across re-establishes.
        string? resumedRound = null;
        void Establish() => sub = workspace.GetMeshNodeStream()
            .Where(n => n?.Content is MeshThread)
            .Take(1)
            .Subscribe(
                node =>
                {
                    if (node?.Content is not MeshThread thread)
                        return;

                    // 🚨 Forcibly run EVERY recovery write below under the thread OWNER's identity
                    // (mirrors ExecRoundWatcher's FromNode scope). The reset / cancel / resume writes
                    // drive an UpdateStreamRequest from the sync hub on a context-less re-establish
                    // continuation; without this they post a null AccessContext → the never-null guard
                    // fails them → a DeliveryFailure storm that faults the observation → it re-establishes
                    // in a tight loop ("[ThreadExec] Init observation faulted for Thread — re-establishing").
                    using var recoveryScope = MeshWeaver.Mesh.Security.AccessContextScope.FromNode(node, accessService, logger);

                    // (1) Honor a cancel that was requested before the hub died,
                    //     before looking at Status.
                    if (thread.RequestedStatus == ThreadExecutionStatus.Cancelled
                        && thread.Status != ThreadExecutionStatus.Cancelled)
                    {
                        logger?.LogInformation(
                            "[ThreadExec] Init: honoring pending cancel on {ThreadPath}", threadPath);
                        HonorPendingCancelOnWake(workspace, cache, node, thread, threadPath, logger);
                        return;
                    }

                    switch (thread.Status)
                    {
                        case ThreadExecutionStatus.Executing
                            when !string.IsNullOrEmpty(thread.ActiveMessageId):
                            // Interrupted mid-round — the in-flight Task.Run is gone.
                            // 🚨 We STAY Executing and re-launch the streaming loop
                            // directly. We must NOT write Executing→StartingExecution:
                            // that is the exact inverse of the _Exec commit edge
                            // (StartingExecution→Executing), and because BOTH this
                            // recovery observer and the exec round watcher are
                            // self-healing, the two volley under load — the re-dispatch
                            // ping-pong (Resubmit/cold-load flake). Resume re-runs the
                            // round into its EXISTING response cell while Status stays
                            // Executing; idempotent per ActiveMessageId so a self-heal
                            // re-read can't double-launch.
                            if (resumedRound == thread.ActiveMessageId)
                                break;
                            resumedRound = thread.ActiveMessageId;
                            logger?.LogInformation(
                                "[ThreadExec] Init: resuming interrupted round on {ThreadPath}, activeMsg={ActiveMsg} (stay Executing)",
                                threadPath, thread.ActiveMessageId);
                            ThreadSubmissionServer.ResumeInterruptedRound(hub, node, logger);
                            break;

                        case ThreadExecutionStatus.Executing:
                            // Executing but no response cell to resume — fall back
                            // to Idle so the submission watcher can claim pending.
                            logger?.LogInformation(
                                "[ThreadExec] Init: Executing with no ActiveMessageId on {ThreadPath} — resetting to Idle",
                                threadPath);
                            workspace.GetMeshNodeStream().Update(n =>
                                n.Content is MeshThread t && t.Status == ThreadExecutionStatus.Executing
                                    ? n with
                                    {
                                        LastModified = DateTime.UtcNow,
                                        Content = t with
                                        {
                                            Status = ThreadExecutionStatus.Idle,
                                            ExecutionStatus = null,
                                            ActiveMessageId = null,
                                            ExecutionStartedAt = null,
                                            StreamingText = null,
                                            StreamingToolCalls = null,
                                        }
                                    }
                                    : n)
                                .Subscribe(_ => { }, ex => logger?.LogWarning(ex,
                                    "[ThreadExec] Init reset: stream.Update failed for {ThreadPath}", threadPath));
                            break;

                        case ThreadExecutionStatus.StartingExecution
                            when string.IsNullOrEmpty(thread.ActiveMessageId):
                            // 🚨 ORPHANED-CLAIM RECOVERY (#539). The submission watcher wrote the
                            // CLAIM (Idle → StartingExecution) but the _Exec round watcher's COMMIT
                            // (StartingExecution → Executing, which allocates the response cell and
                            // stamps ActiveMessageId in ONE write) never landed: the previous hub
                            // incarnation died between claim and commit (a NodeType release recycled
                            // the hub, a redeploy, a grain migration), OR the _Exec watcher missed
                            // the REPLAYED claim on cold activation. Nothing re-drives the commit —
                            // the "first emission" the old no-op relied on already happened for a
                            // dead incarnation — so the thread parks at StartingExecution forever:
                            // ThreadStateTransitions treats it as in-flight, so resubmission is
                            // refused AND the submission watcher (Idle/Cancelled only) won't re-claim.
                            // The cancel path already covers this window (the No-CTS claim-window
                            // fallback in InstallCancellationWatcher); normal resumption did not.
                            //
                            // Activation is PROOF that no round is actually in flight (executions die
                            // with their hub). An orphaned claim has NO cell (ActiveMessageId is
                            // stamped only by the commit), so roll it back to Idle — a LEGAL edge
                            // (ThreadStateTransitions: "StartingExecution → Idle, rollback: claim
                            // found nothing"), the same idempotent shape as the Executing-without-cell
                            // reset above. The submission watcher then re-claims the still-queued
                            // PendingUserMessages as a fresh, LIVE Idle → StartingExecution
                            // transition, which the freshly-subscribed _Exec round watcher reliably
                            // observes (a cold-load replay it may have missed now arrives as a live
                            // emission), so the round runs.
                            //
                            // 🚨 CAS-guarded: roll back ONLY while STILL StartingExecution with no
                            // cell. If the _Exec commit has since flipped StartingExecution →
                            // Executing (cell allocated, ActiveMessageId set) the guard fails and this
                            // is a no-op — we NEVER write Executing → StartingExecution (that inverse
                            // of the commit edge is the re-dispatch ping-pong). This recovery applies
                            // ONLY to an orphaned claim on a freshly activated hub, before any commit.
                            logger?.LogWarning(
                                "[ThreadExec] Init: recovered orphaned StartingExecution claim on {ThreadPath} " +
                                "after hub reactivation (no cell allocated — the _Exec commit was lost) — " +
                                "re-queuing: rolling back to Idle so the submission watcher re-claims the round.",
                                threadPath);
                            workspace.GetMeshNodeStream().Update(n =>
                                n.Content is MeshThread t
                                    && t.Status == ThreadExecutionStatus.StartingExecution
                                    && string.IsNullOrEmpty(t.ActiveMessageId)
                                    ? n with
                                    {
                                        LastModified = DateTime.UtcNow,
                                        Content = t with
                                        {
                                            Status = ThreadExecutionStatus.Idle,
                                            ExecutionStatus = null,
                                            ExecutionStartedAt = null,
                                        }
                                    }
                                    : n)
                                .Subscribe(_ => { }, ex => logger?.LogWarning(ex,
                                    "[ThreadExec] Init orphan-claim rollback: stream.Update failed for {ThreadPath}", threadPath));
                            break;

                        default:
                            // StartingExecution (with a cell — the commit already
                            // ran, the round is effectively Executing) / Idle /
                            // Cancelled / Done — no write; the relevant watcher (or
                            // terminal state) already covers it.
                            break;
                    }
                },
                ex =>
                {
                    // Observer died before we read & drove the loaded state — RESTART.
                    // (User directive: any observer dying before the thread reaches a
                    // terminal/valid state must restart the watcher.) Without this a
                    // faulted observation left the stale thread stuck forever.
                    //
                    // 🚨 Two guards make this self-heal safe — mirroring the sanctioned
                    // SubscribeWithReEstablish pattern (disposed-terminal + scheduled,
                    // never-synchronous re-establish):
                    //   • `disposed` stops re-establishing once the hub is torn down —
                    //     the fault is then permanent (scope gone), so retrying is futile.
                    //   • the 1 s Timer hops the re-establish OFF the synchronous error
                    //     stack. A Subscribe-time fault re-entering Establish inline would
                    //     recurse to a stack overflow; deferring also lets the disposal
                    //     hook set `disposed` past the teardown window.
                    if (disposed)
                        return;
                    logger?.LogWarning(ex,
                        "[ThreadExec] Init observation faulted for {ThreadPath} — re-establishing recovery",
                        threadPath);
                    System.Reactive.Linq.Observable.Timer(TimeSpan.FromSeconds(1))
                        .Subscribe(_ => { if (!disposed) Establish(); });
                });

        Establish();

        // Guarantee-terminal watchdog. Belt-and-suspenders for the case where the
        // initial recovery fired but the resumed round still never reached a
        // terminal state — a missed StartingExecution dispatch, an observer that
        // died mid-round, a child whose completion the parent never saw. If the
        // thread node goes SILENT (no emission = no progress) for the grace period
        // while still IsExecuting, the round is wedged: force it to Idle so
        // IsExecuting clears and the user can resubmit. Two false-positive guards:
        //   • Throttle resets on EVERY node emission, so a healthy streaming round
        //     (which bumps LastModified continuously) never trips it.
        //   • A thread legitimately waiting on a child delegation is silent by
        //     design — that staleness is the HeartbeatTicker's job (it cancels the
        //     stale sub-thread), so we skip threads with an unfinished delegation.
        var watchdog = workspace.GetMeshNodeStream()
            .Throttle(StuckGracePeriod)
            // ContentAs (not `Content as`): a degraded JsonElement own-node would make the
            // cast silently null → the watchdog goes inert and a wedged round never recovers.
            .Select(n => n.ContentAs<MeshThread>(hub.JsonSerializerOptions, logger))
            .Where(t => t is { IsExecuting: true } && !HasUnfinishedDelegation(t))
            .Subscribe(
                _ =>
                {
                    logger?.LogWarning(
                        "[ThreadExec] Init watchdog: {ThreadPath} wedged non-terminal with no progress " +
                        "for {Grace:F0}s — forcing Idle (guarantee terminal).",
                        threadPath, StuckGracePeriod.TotalSeconds);
                    workspace.GetMeshNodeStream().Update(node =>
                    {
                        var t = node.ContentAs<MeshThread>(hub.JsonSerializerOptions, logger);
                        return t is { IsExecuting: true } && !HasUnfinishedDelegation(t)
                            ? node with
                            {
                                LastModified = DateTime.UtcNow,
                                Content = t with
                                {
                                    Status = ThreadExecutionStatus.Idle,
                                    ExecutionStatus = null,
                                    ActiveMessageId = null,
                                    ExecutionStartedAt = null,
                                    StreamingText = null,
                                    StreamingToolCalls = null,
                                }
                            }
                            : node;
                    })
                        // #147 escape hatch. The force-Idle rescue write above rides the hub's own
                        // action block — which on Orleans runs on the grain's
                        // ActivationTaskScheduler. In the wedge this watchdog exists for (a
                        // streaming continuation that never resumed occupying the scheduler slot,
                        // 1376-message backlog), the rescue write joins the blocked queue and can
                        // NEVER land: the watchdog fired correctly but could not rescue. A healthy
                        // hub applies an own-node Update in milliseconds, so a write that neither
                        // completes within WatchdogRescueBudget nor errors is proof the action
                        // block is dead → escalate OUT-OF-BAND: RequestGrainDeactivation invokes
                        // Grain.DeactivateOnIdle via the callback the grain registered at
                        // activation (this Throttle emission runs on a ThreadPool timer, off the
                        // blocked scheduler). Deactivation disposes the hub → RegisterForDisposal
                        // fires executionCts.Cancel() → the stuck call is torn down; the next
                        // access re-activates fresh and this same recovery drives the persisted
                        // state to a valid one. In monolith hosting no callback is registered —
                        // RequestGrainDeactivation returns false (no-op) and the rescue write
                        // above is the whole story, as before.
                        .Timeout(WatchdogRescueBudget)
                        .Subscribe(_ => { }, ex =>
                        {
                            // Disposal already tearing the hub down: this inner rescue chain's
                            // Timeout timer is NOT owned by the outer watchdog subscription (an
                            // inner Subscribe created in onNext survives the outer's Dispose), so
                            // it can fire AFTER hub disposal. There is nothing left to rescue —
                            // disposal is the escape hatch's own goal — and escalating would
                            // resolve services from the disposed container and throw INSIDE this
                            // OnError on a bare timer thread (process-fatal). Refuse-and-complete.
                            if (disposed || hub.IsDisposing)
                                return;
                            // Escalate ONLY on the landing-budget timeout — that is the proof the
                            // action block is blocked. Any other error (validation, access,
                            // serialization) fails FAST on a healthy hub: deactivating there would
                            // tear down a healthy grain for a defect deactivate-and-retry cannot
                            // cure. Those keep the log-and-give-up behavior.
                            if (ex is not TimeoutException)
                            {
                                logger?.LogWarning(ex,
                                    "[ThreadExec] Init watchdog: force-Idle Update failed for " +
                                    "{ThreadPath} (non-timeout — hub is responsive, not escalating).",
                                    threadPath);
                                return;
                            }
                            logger?.LogWarning(ex,
                                "[ThreadExec] Init watchdog: force-Idle Update did not land within " +
                                "{Budget:F0}s for {ThreadPath} — the hub's action block is blocked (#147).",
                                WatchdogRescueBudget.TotalSeconds, threadPath);
                            if (hub.RequestGrainDeactivation())
                                logger?.LogWarning(
                                    "[ThreadExec] Init watchdog: requested OUT-OF-BAND grain " +
                                    "deactivation for wedged {ThreadPath} — hub disposal will cancel " +
                                    "the round's CancellationTokenSource and the grain re-activates " +
                                    "fresh on next access (#147).",
                                    threadPath);
                            else
                                logger?.LogWarning(
                                    "[ThreadExec] Init watchdog: no grain deactivation callback " +
                                    "registered for {ThreadPath} (monolith hosting) — rescue write " +
                                    "failed and no escape hatch is available.",
                                    threadPath);
                        });
                },
                ex => logger?.LogWarning(ex,
                    "[ThreadExec] Init watchdog stream faulted for {ThreadPath}", threadPath));

        hub.RegisterForDisposal(_ => { disposed = true; sub?.Dispose(); watchdog.Dispose(); });
    }

    /// <summary>
    /// Grace period for the guarantee-terminal watchdog. A thread node that emits
    /// nothing (no progress) for this long while still <see cref="MeshThread.IsExecuting"/>
    /// is treated as wedged and forced to Idle. Generous so it never races a slow
    /// but live round — healthy work bumps <c>LastModified</c> far more often.
    /// </summary>
    private static readonly TimeSpan StuckGracePeriod = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Budget for the stuck-round watchdog's force-Idle rescue write to LAND. A healthy
    /// hub applies an own-node <c>stream.Update</c> in milliseconds; when the write does
    /// not complete within this window the hub's action block itself is blocked — the
    /// #147 wedge, where the grain's ActivationTaskScheduler slot is occupied by a
    /// continuation that never resumed, so the rescue write joins the dead backlog and
    /// can never be processed. Past this budget the watchdog escalates to the
    /// out-of-band grain deactivation escape hatch
    /// (<see cref="MessageHubExtensions.RequestGrainDeactivation"/>).
    /// </summary>
    private static readonly TimeSpan WatchdogRescueBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Hard wall-clock cap on a single streaming round at the AI I/O boundary (#147).
    /// The primary defect behind the permanently-wedged thread grain was an UNBOUNDED
    /// external network wait: an LLM streaming call that never produced another update
    /// (and never faulted) parked its continuation forever, occupying the grain's
    /// scheduler slot — 1376 messages queued, recovery only via pod restart. With this
    /// cap the continuation ALWAYS resumes: the round's linked CancellationTokenSource
    /// fires and the round terminates as a graceful ERROR (response cell
    /// <c>Status = Error</c> naming the timeout), so the wedge cannot form.
    /// <para>Generous enough for legitimate long rounds INCLUDING nested delegation
    /// trees (a delegating parent holds its round open while sub-threads work) —
    /// deliberately matched to the grain-side backstop
    /// <c>MessageHubGrain.MaxLongRunningOperationDuration</c> (30 min), so the
    /// in-process graceful error fires no later than the grain's own
    /// stop-extending-lifetime cap. Tests override via <see cref="AiStreamingLimits"/>
    /// (mesh-scoped singleton, never a mutable static).</para>
    /// </summary>
    private static readonly TimeSpan MaxStreamingDuration = TimeSpan.FromMinutes(30);

    /// <summary>
    /// True when the thread carries an in-flight <c>delegate_to_agent</c> tool call
    /// (a streaming tool call with a <see cref="ToolCallEntry.DelegationPath"/> and
    /// no <see cref="ToolCallEntry.Result"/> yet). Such a thread is silent by
    /// design while its child sub-thread runs; the <see cref="InstallHeartbeatTicker"/>
    /// path — not the stuck-watchdog — owns its staleness.
    /// </summary>
    private static bool HasUnfinishedDelegation(MeshThread t) =>
        t.StreamingToolCalls is { Count: > 0 }
        && t.StreamingToolCalls.Any(tc =>
            tc.Result is null && !string.IsNullOrEmpty(tc.DelegationPath));

    /// <summary>
    /// Round-end reconcile for the in-memory inbox channel: folds the ids
    /// <c>check_inbox</c> drained this round (delivered INLINE) into the SAME terminal
    /// thread-node write — removes them from <see cref="MeshThread.PendingUserMessages"/>
    /// and adds them to <see cref="MeshThread.IngestedMessageIds"/> (de-duplicated,
    /// order preserved). This is the ONLY place the consumed follow-ups touch the node;
    /// <c>check_inbox</c> itself writes nothing mid-round.
    ///
    /// <para>🚨 Deliberately does NOT add the ids to <see cref="MeshThread.Messages"/>:
    /// an inline-delivered follow-up gets no satellite cell, and a dangling Messages ref
    /// without a backing node re-triggers the missing-node NotFound storm this work
    /// fixes. Messages that were channeled but NOT consumed (or arrived after the last
    /// <c>check_inbox</c>) are left in <c>PendingUserMessages</c> so the submission
    /// watcher dispatches a fresh round for them — nothing is lost.</para>
    ///
    /// <para>Called with a snapshot captured ONCE before the (possibly retried) terminal
    /// <c>Update</c> lambda, so the fold is deterministic across optimistic-concurrency
    /// retries.</para>
    /// </summary>
    private static MeshThread FoldConsumedInbox(MeshThread t, ImmutableList<string> consumedIds)
    {
        if (consumedIds.IsEmpty) return t;
        var pending = t.PendingUserMessages;
        foreach (var id in consumedIds)
            pending = pending.Remove(id);
        var ingested = t.IngestedMessageIds;
        foreach (var id in consumedIds)
            if (!ingested.Contains(id))
                ingested = ingested.Add(id);
        // Restore UserMessageIds ⊇ IngestedMessageIds (a consumed id should already be in
        // UserMessageIds, but be defensive like CommitRoundAndExecute — a concurrent
        // cross-mirror RFC 7396 array-replace can have dropped it from the derived list).
        var userIds = t.UserMessageIds;
        foreach (var id in ingested)
            if (!userIds.Contains(id))
                userIds = userIds.Add(id);
        return t with
        {
            PendingUserMessages = pending,
            IngestedMessageIds = ingested,
            UserMessageIds = userIds
        };
    }

    /// <summary>
    /// Wake-up branch for a thread that has a pending <c>RequestedStatus =
    /// Cancelled</c> the previous activation never got to honor. Stamps the
    /// active response cell <see cref="ThreadMessageStatus.Cancelled"/> (marking
    /// any unfinished tool calls) and writes the terminal thread state
    /// (<c>Status = Cancelled, RequestedStatus = null, ActiveMessageId = null</c>),
    /// leaving <c>PendingUserMessages</c> intact so the submission watcher
    /// re-dispatches a fresh round.
    /// </summary>
    private static void HonorPendingCancelOnWake(
        IWorkspace workspace, IMeshNodeStreamCache cache, MeshNode node, MeshThread thread,
        string threadPath, ILogger? logger)
    {
        if (!string.IsNullOrEmpty(thread.ActiveMessageId))
        {
            var responsePath = $"{threadPath}/{thread.ActiveMessageId}";
            var responseMsgId = thread.ActiveMessageId!;
            var mainEntity = node.MainNode ?? threadPath;
            var cancelledToolCalls = thread.StreamingToolCalls?
                .Select(tc => tc.Result != null
                    ? tc
                    : tc with { Result = "Cancelled (server restarted)", IsSuccess = false })
                .ToImmutableList() ?? ImmutableList<ToolCallEntry>.Empty;

            UpdateResponseCell(workspace.Hub, responsePath, threadPath, responseMsgId, mainEntity,
                msg => msg with
                {
                    Text = msg.Text ?? "",
                    ToolCalls = cancelledToolCalls,
                    Status = ThreadMessageStatus.Cancelled,
                    CompletedAt = DateTime.UtcNow
                },
                logger);
        }

        workspace.GetMeshNodeStream().Update(n =>
            n.Content is MeshThread t && t.Status != ThreadExecutionStatus.Cancelled
                ? n with
                {
                    LastModified = DateTime.UtcNow,
                    Content = t with
                    {
                        Status = ThreadExecutionStatus.Cancelled,
                        RequestedStatus = null,
                        ExecutionStatus = null,
                        ActiveMessageId = null,
                        ExecutionStartedAt = null,
                        StreamingText = null,
                        StreamingToolCalls = null,
                        Summary = "Cancelled (server restarted)."
                    }
                }
                : n)
            .Subscribe(_ => { }, ex => logger?.LogWarning(ex,
                "[ThreadExec] HonorPendingCancelOnWake: stream.Update failed for {ThreadPath}", threadPath));
    }

    // WatchForExecution deleted as part of the "one trigger via GetMeshNodeStream"
    // unification. The legacy auto-execute hook for BuildThreadWithMessages used
    // PendingUserMessage (singular) + Status=Executing pre-set at construction
    // time, then competed with the submission watcher by creating cells +
    // calling ExecuteMessageAsync directly. BuildThreadWithMessages now seeds
    // PendingUserMessages (dict) at Status=Idle and lets InstallServerWatcher
    // claim through the standard flow — a single trigger path for every
    // thread, no message-type or watcher rivalry.

    // HandleSubmitMessage + HandleSubmitMessageLegacy deleted 2026-05-25.
    // HandleStartExecutionOnExec deleted with the trigger removal — _Exec's
    // round watcher (InstallExecRoundWatcher) subscribes to the parent thread
    // node's stream and fires DispatchAfterClaim on each Idle → StartingExecution
    // transition. Public submissions go through ThreadSubmission.Submit →
    // ThreadInput.AppendUserInput → the submission watcher; _Exec calls
    // ExecuteMessageAsync directly (method, not message).

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
    /// Per-round mutable handle to the response cell currently being streamed
    /// into. Stored on the parent thread hub via <c>hub.Set</c> for the duration
    /// of a round. The streaming writer reads <see cref="ResponseMsgId"/> and
    /// <see cref="TextBaseline"/> on every push. <c>check_inbox</c> (mid-round)
    /// appends any in-flight user message directly into <see cref="ResponseText"/>
    /// (the live accumulator) with a marker, so the agent's continuation renders
    /// below the user's interjection in the SAME response cell — Claude-Code style,
    /// no separate cells and no output-cell split. <see cref="ResponseText"/> is
    /// null outside the streaming window.
    /// </summary>
    internal sealed class ActiveResponseSegment(string responseMsgId)
    {
        public string ResponseMsgId { get; set; } = responseMsgId;
        public int TextBaseline { get; set; }
        public System.Text.StringBuilder? ResponseText { get; set; }
    }

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
        string? Harness,
        string? ContextPath,
        IReadOnlyList<string>? Attachments,
        string? ContextReference = null);

    /// <summary>
    /// Runs ONE agent round as a cold observable that completes when the round's terminal
    /// Status write settles (OnError when the round faults or its terminal write fails).
    /// <para>🚨 SUBSCRIBE EXACTLY ONCE per round. The pipeline is cold with heavy side effects
    /// per subscription — a second Subscribe launches a SECOND round (double pool invoke,
    /// double client init, duplicate cell writes). The single call site
    /// (<c>ThreadSubmissionServer.CommitRoundAndExecute</c>) guards with
    /// <c>didCommitThisEmission</c>; any new caller must provide an equivalent single-fire
    /// guarantee.</para>
    /// </summary>
    internal static IObservable<System.Reactive.Unit> ExecuteMessageAsync(
        IMessageHub hub,
        RoundParams request,
        AccessContext? userAccessContext)
    {
        // Selections arrive as picked node PATHS ("Harness/MeshWeaver",
        // "Provider/Anthropic/claude-…", "Agent/Assistant", "AgenticPension/Agent/Datenextraktion").
        // The HARNESS matches a bare REGISTERED id (last path segment), so it normalizes here.
        // The MODEL and the AGENT do NOT: two catalog entries can share a bare id (a keyless global
        // Provider/OpenAICompatible/qwen3.6:latest vs a user's keyed copy, or the same id under two
        // providers), so collapsing to the bare id resolves the WRONG node — the model must resolve by
        // FULL PATH. AgentChatClient.Initialize already does this (ChatClientCredentialResolver.
        // ResolveModelId → node-path lookup → the exact node's wire id + credentials + capabilities);
        // stripping the path here defeated it. Keep the model path; the resolver collapses it to the
        // wire id at the exact node. (The cell-stamp display name is normalized to the short name at
        // write time in PushToResponseMessage, so the persisted friendly name is unaffected.)
        request = request with
        {
            ModelName = request.ModelName,
            Harness = SelectionId.IdOf(request.Harness)
        };
        var parentHub = hub.Configuration.ParentHub!;
        var threadPath = request.ThreadPath;
        var logger = parentHub.ServiceProvider.GetRequiredService<ILogger<AgentChatClient>>();
        var cache = parentHub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var responseMsgId = request.ResponseMessageId
            ?? throw new InvalidOperationException(
                $"ExecuteMessageAsync: RoundParams for thread {threadPath} has no ResponseMessageId");
        var responsePath = $"{threadPath}/{responseMsgId}";

        // Active response-cell segment for THIS round. The streaming writer
        // (PushToResponseMessage) targets segment.ResponseMsgId, NOT the captured
        // responseMsgId, so the check_inbox tool can split the output mid-round:
        // it freezes the current cell, inserts the interrupting user cells, and
        // switches segment.ResponseMsgId to a fresh cell (clearing the shared
        // StringBuilder) so subsequent tokens stream into the new cell. Stored on
        // the parent hub so InboxTool.CheckInbox can reach it. See A7 in
        // ThreadOperations.md.
        var segment = new ActiveResponseSegment(responseMsgId);
        parentHub.Set(segment);

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
        // 🚨 Circuit breaker for a DEAD response cell. If the cell's per-node hub never delivers
        // initial state, cache.Update throws "no initial state arrived" INSTANTLY on every push —
        // and the streaming loop pushes once per token, so without this the round STORMS failed
        // writes (seq 744,745,746… + [STALE-CALLBACK], flooding Loki and backing up the mesh/cache
        // hub until the portal wedges — the failure the user hit). Latch on the FIRST such permanent
        // failure: every later push short-circuits to Observable.Empty (no cache.Update, no storm),
        // we log ONCE at Info (a dead cell is a graceful degradation, not a per-token warning
        // flood), and the round ends normally. Interlocked because the update-error callback and the
        // next push can run on different hub threads. (Follow-up: also cancel the in-flight LLM
        // stream once latched, to stop spending the model call on a response nobody can read.)
        var responseCellDead = 0;   // 0 = writable, 1 = permanently unavailable (no initial state)
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
            string? summary = null,
            string? harness = null,
            int? cacheReadTokens = null, int? cacheWriteTokens = null,
            bool stampActivity = true)
        {
            // Cell already latched dead (no initial state ever arrived) — skip the write entirely.
            // Never construct/subscribe a doomed cache.Update; that is the storm. Empty completes
            // without emitting, which every caller here handles (all Subscribe fire-and-forget, no
            // FirstAsync), so a dead cell simply produces no further writes and the round ends.
            if (System.Threading.Volatile.Read(ref responseCellDead) != 0)
                return System.Reactive.Linq.Observable.Empty<MeshNode>();

            // Re-read the segment's CURRENT target + text baseline on every push so
            // writes follow a mid-round check_inbox split to the new cell. The cell
            // receives only the accumulated text PAST the baseline (the prior cells'
            // text was committed when they were frozen). A stale buffered push whose
            // text is shorter than the baseline slices to empty — harmless.
            var curResponseMsgId = segment.ResponseMsgId;
            var baseline = segment.TextBaseline;
            var curResponsePath = $"{threadPath}/{curResponseMsgId}";
            text = text.Length > baseline ? text[baseline..] : string.Empty;
            logger.LogDebug("[ThreadExec] PUSH_TO_MSG: responsePath={ResponsePath}, textLen={TextLen}, toolCalls={ToolCalls}, updatedNodes={UpdatedNodes}, status={Status}",
                curResponsePath, text.Length, toolCalls.Count, updatedNodes.Count, status?.ToString() ?? "(preserve)");

            // 🚨 Re-seed the user AccessContext immediately before the cell write. Every caller of
            // this helper reaches it via a Reactive continuation — the streaming Sample(100ms)
            // callback, the init-phase history/contextNode SelectMany ("Generating response..."), and
            // the terminal completion path — each running on a hub-pipeline / ThreadPool thread where
            // the per-hub AsyncLocal Context has been flipped to a per-cell impersonated address or
            // dropped entirely (worse under a starved 2-core runner). MeshNodeStreamHandle.Update
            // captures the AMBIENT context at THIS point and carries it onto the cross-hub
            // UpdateStreamRequest; if it's null the owner's PostPipeline fails the write closed
            // ("hub=sync/… UpdateStreamRequest … no AccessContext") → the round faults → the cell shows
            // "Agent initialization stalled" and IsExecuting never clears (the 2-core CI flake in
            // OrleansSubThreadAutoResume / Reentrancy / NodeChangePropagation). A thread cell is ALWAYS
            // owned by the thread user, so re-asserting userAccessContext is the correct identity.
            // See AccessContextPropagation.md / feedback_access_context_always_set.
            if (userAccessContext != null)
                parentHub.ServiceProvider.GetService<AccessService>()?.SetContext(userAccessContext);

            // 🚨 Route the cell write through parentHub (the thread hub), NOT
            // `hub` (the _Exec hosted hub). _Exec is created with no AddData, so
            // hub.GetMeshNodeStream(...) → hub.GetWorkspace() throws
            // "Configuration of message hub is inconsistent: AddData was not
            // called." parentHub owns the workspace + resolves the same
            // process-wide IMeshNodeStreamCache, so the cross-hub patch routes
            // through the identical shared handle the GUI reads from.
            var updateObs = parentHub.GetMeshNodeStream(curResponsePath).Update(node =>
            {
                var current = node.ContentAs<ThreadMessage>(parentHub.JsonSerializerOptions, logger);
                // Existing node whose content can't be recovered → leave it alone, NEVER clobber.
                if (node?.Content is not null && current is null)
                    return node;
                current ??= new ThreadMessage
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
                // 🚨 UpdatedNodes accumulate — never replace. Like ToolCalls (merged
                // above) and Text (monotonic), node changes must not regress: the
                // trailing Sample(100ms) emission fires AFTER the terminal completion
                // push (snapshots.OnCompleted flushes the last buffered snapshot), and
                // an earlier/empty snapshot would otherwise CLOBBER the aggregated
                // changes back to []. Union by path (min VersionBefore / max
                // VersionAfter, last Operation wins) so an empty incoming push is a
                // no-op and re-pushes of the same node coalesce to one entry — exactly
                // what OrleansNodeChangePropagationTest's ContainSingle assertion wants.
                var mergedNodes = updatedNodes.IsEmpty
                    ? current.UpdatedNodes
                    : AggregateNodeChanges(current.UpdatedNodes.AddRange(updatedNodes));
                var updatedContent = current with
                {
                    Text = nextText,
                    ToolCalls = mergedToolCalls,
                    UpdatedNodes = mergedNodes,
                    // The agent is carried through as a full PATH for resolution; the
                    // persisted/displayed cell author is the friendly short name (last segment).
                    AgentName = SelectionId.IdOf(agentName) ?? current.AgentName,
                    ModelName = modelName ?? current.ModelName,
                    Harness = harness ?? current.Harness,
                    InputTokens = inputTokens ?? current.InputTokens,
                    OutputTokens = outputTokens ?? current.OutputTokens,
                    TotalTokens = totalTokens ?? current.TotalTokens,
                    CacheReadTokens = cacheReadTokens ?? current.CacheReadTokens,
                    CacheWriteTokens = cacheWriteTokens ?? current.CacheWriteTokens,
                    CompletedAt = completedAt ?? current.CompletedAt,
                    Status = nextStatus,
                    Summary = summary ?? current.Summary
                };
                return node != null
                    ? node with { Content = updatedContent }
                    : new MeshNode(curResponseMsgId, threadPath)
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
                ex =>
                {
                    // A "no initial state arrived" TimeoutException means the response cell's
                    // per-node hub will NEVER deliver — the cell is permanently unwritable. Trip
                    // the circuit breaker ONCE (so every later push short-circuits and the storm
                    // stops), and log at Info: this is a graceful degradation, not a fault to
                    // flood the log with a warning per token.
                    var isDeadCell = ex is TimeoutException
                        && ex.Message.Contains("no initial state arrived", StringComparison.Ordinal);
                    if (isDeadCell)
                    {
                        if (System.Threading.Interlocked.Exchange(ref responseCellDead, 1) == 0)
                            logger.LogInformation(
                                "[ThreadExec] response cell {Path} unavailable (no initial state arrived); " +
                                "ending this round gracefully — no further streaming writes to it", curResponsePath);
                        // already latched → stay silent, no per-token spam
                    }
                    else
                    {
                        // A transient/other failure — keep the existing loud signal (one-off).
                        logger.LogWarning(ex, "[ThreadExec] cache.Update failed for {Path}", curResponsePath);
                    }
                });

            // Heartbeat stamp on the OWN thread. Throttled to one write per
            // heartbeatStampInterval so the streaming hot path (Sample(100ms))
            // doesn't spam thread-node writes — one per interval is plenty
            // since the heartbeat scanner runs every 5 s and the timeout is
            // 30 s. Single source of "is this sub-thread still alive?" the
            // parent's heartbeat scanner reads via cache.GetStream(threadPath).
            //
            // 🚨 stampActivity=false for the framework's own "Generating response..."
            // placeholder: it is NOT agent progress. Stamping it would set
            // LastActivityAt at round start, so a sub-agent still in first-token
            // latency looks "active then stalled" to the parent heartbeat and gets
            // cancelled by the inter-activity timeout before it ever emits (the
            // "sub-thread never started" symptom). Leaving LastActivityAt null until
            // the FIRST real token is what lets DelegationHandlers tell first-token
            // latency (the generous first-activity budget) apart from a genuine stall.
            var now = DateTime.UtcNow;
            if (stampActivity && now - lastActivityStamped > heartbeatStampInterval)
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
            // Re-seed the user AccessContext before the thread-node write — same reason as
            // PushToResponseMessage above. All terminal callers (Completed / Cancelled / Error /
            // NothingToSend) reach this from a Reactive continuation where the AsyncLocal Context was
            // flipped/dropped; the write must carry the thread owner. Observable.Defer so the reseed
            // runs at SUBSCRIBE time (when the cold Update is actually invoked), not at the (often
            // earlier, differently-threaded) point the observable is constructed.
            System.Reactive.Linq.Observable.Defer(() =>
            {
                if (userAccessContext != null)
                    parentHub.ServiceProvider.GetService<AccessService>()?.SetContext(userAccessContext);
                return parentHub.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
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
            // placeholder + 10s-timeout IMeshQueryCore.Query scan).
            var c = new AgentChatClient(parentHub.ServiceProvider, priorMessages: null);
            c.SetThreadId(threadPath);
            parentHub.Set(c);
            clientObs = Observable.Return(c);
        }

        // 🔁 Resume recovery: a crash-resume re-launches an interrupted round into its
        // EXISTING response cell and carries NO fresh selection (PendingUserMessages was
        // already drained before the interruption, so PlanNextRound had no message to read
        // it from). The response cell IS the persisted single source of truth for what
        // agent/model/harness that round used — recover the missing selection from it
        // rather than from a thread-level Pending* mirror (which no longer exists). Only
        // reads the cell when something is actually missing; the normal submit path carries
        // the full selection on the drained message and skips this read entirely.
        IObservable<RoundParams> requestObs;
        if (string.IsNullOrEmpty(request.AgentName)
            && string.IsNullOrEmpty(request.ModelName)
            && string.IsNullOrEmpty(request.Harness))
        {
            requestObs = parentHub.GetMeshNodeStream(responsePath)
                .Select(n => n?.ContentAs<ThreadMessage>(parentHub.JsonSerializerOptions, logger))
                .Where(m => m is not null)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(5))
                .Select(cell => request with
                {
                    AgentName = request.AgentName ?? cell!.AgentName,
                    // The persisted cell carries the COMPOSER form of the model — the full
                    // node path ("Provider/OpenAICompatible/qwen-small"). Pass it through RAW,
                    // exactly like the normal submit path: AgentChatClient.Initialize normalizes
                    // via the credential resolver's node-path lookup. SelectionId.IdOf here
                    // mangled org/model-shaped wire ids ("z-ai/glm-5.2" → "glm-5.2"), which
                    // resolve no credentials — the "ApiKey is missing" round failure.
                    ModelName = request.ModelName ?? cell!.ModelName,
                    Harness = SelectionId.IdOf(request.Harness ?? cell!.Harness)
                })
                .Catch<RoundParams, Exception>(ex =>
                {
                    logger.LogWarning(ex,
                        "[ThreadExec] Resume: could not recover selection from response cell {ResponsePath}; proceeding with defaults",
                        responsePath);
                    return Observable.Return(request);
                });
        }
        else
        {
            requestObs = Observable.Return(request);
        }

        // Composed, returned round observable — completes when the round reaches a terminal
        // Status (Completed/Cancelled/Error) and surfaces real faults via OnError. The submission
        // watcher Subscribes to this (no fire-and-forget): it owns the subscription + disposal.
        return requestObs.SelectMany(recovered =>
        {
        request = recovered;
        return clientObs.Take(1).SelectMany(chatClient =>
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
            return chatClient.WhenInitialized
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(60))
                .SelectMany(client =>
                {
                logger.LogDebug("[ThreadExec] Agents ready for {ThreadPath}, starting execution", threadPath);

                // 🚦 Harness dispatch. A harness is NOT a model provider: Claude Code /
                // GitHub Copilot run their OWN CLI library (ClaudeCodeChatClient /
                // CopilotChatClient) and must bypass the model-provider factory chain.
                // The MeshWeaver harness returns null → keep the agent/model client.
                // This is the fix for "harness selected → Azure DeploymentNotFound":
                // the round no longer routes a harness through a provider.
                var selectedHarness = HarnessNodeType.ResolveHarness(parentHub.ServiceProvider, request.Harness);
                // CLI harnesses (Claude Code / Copilot) return their own IChatClient;
                // the MeshWeaver harness returns null → use the AgentChatClient (which
                // has its own non-IChatClient streaming signature). harnessClient stays
                // null for MeshWeaver so the existing agent path runs unchanged.
                // 🔎 A harness was SPECIFIED but does not resolve to any registered IHarness —
                // say it was NOT FOUND (a stale/renamed id, e.g. an old "Claude Code" path before
                // the slug fix, or a CLI harness whose feature flag is off). Fall back to the
                // default agent path rather than crashing or silently ignoring it. (Empty harness
                // ⇒ no selection ⇒ default; not a "not found".)
                if (selectedHarness is null && !string.IsNullOrEmpty(request.Harness))
                    logger.LogWarning(
                        "[ThreadExec] Harness '{Harness}' not found (no registered IHarness with that id) for " +
                        "{ThreadPath} — falling back to the default agent path", request.Harness, threadPath);

                // 🛡️ A harness that can't build its client (CLI missing, bad config, a bad
                // harness id/path) must NOT crash the round or wedge the hub — catch, log, and
                // fall back to the default MeshWeaver agent path (harnessClient stays null) so
                // the user still gets a response. No retry/resubscribe here ⇒ no storm.
                IChatClient? harnessClient = null;
                try
                {
                    harnessClient = selectedHarness?.CreateChatClient(
                        new HarnessExecutionContext(parentHub, null, request.ModelName));
                }
                catch (Exception harnessEx)
                {
                    logger.LogError(harnessEx,
                        "[ThreadExec] Harness '{Harness}' threw building its chat client for {ThreadPath}; " +
                        "falling back to the default agent path", request.Harness, threadPath);
                }
                if (harnessClient != null)
                    logger.LogInformation(
                        "[ThreadExec] Harness '{Harness}' → {Client} (bypassing provider chain) for {ThreadPath}",
                        request.Harness, harnessClient.GetType().Name, threadPath);

                // Set context from remote stream — must subscribe (Current is null on cold streams).
                // When ContextPath is empty we just set null; otherwise wait for the first emission
                // (with a short timeout fallback so a missing/inaccessible node doesn't stall execution)
                // before continuing with SetExecutionContext + history load.
                IObservable<MeshNode?> contextNodeObs;
                if (!string.IsNullOrEmpty(request.ContextPath))
                {
                    // Read context node via the typed handle (routes cross-hub
                    // through the shared cache, deserializes Content). 🚨 Use
                    // parentHub — `hub` is the _Exec hosted hub which has no
                    // AddData, so hub.GetMeshNodeStream → hub.GetWorkspace()
                    // throws "AddData was not called" (the throw escaped on the
                    // WhenInitialized onNext path, wedging every round —
                    // all thread tests timed out).
                    contextNodeObs = parentHub.GetMeshNodeStream(request.ContextPath)
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

                return contextNodeObs.SelectMany(contextNode =>
                {
                    if (!string.IsNullOrEmpty(request.ContextPath))
                    {
                        // Deserialize the carried navigation reference (layout area + query params
                        // as KVP) and assemble the full agent context: main-node address + area +
                        // params + the loaded node identity. Reference only — content via Get.
                        NavigationReference? navRef = null;
                        if (!string.IsNullOrEmpty(request.ContextReference))
                        {
                            try
                            {
                                navRef = JsonSerializer.Deserialize<NavigationReference>(
                                    request.ContextReference, parentHub.JsonSerializerOptions);
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex,
                                    "[ThreadExec] Failed to parse ContextReference for {ContextPath}; shipping address+node only",
                                    request.ContextPath);
                            }
                        }

                        client.SetContext(
                            NavigationContextProjection.ToAgentContext(request.ContextPath, navRef, contextNode));
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
                return LoadFullConversationHistoryFromMesh(parentHub, threadPath,
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
                    .SelectMany(history =>
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
                //
                // RESUME path: UserMessageText is empty and UserMessageId is null —
                // the interrupted round's user message is still in history (nothing
                // was excluded), so we append NO trailing user message (an empty one
                // would be a malformed final turn). The agent simply re-generates a
                // response to the existing last user turn.
                var allMessages = string.IsNullOrEmpty(request.UserMessageText)
                    ? chatHistory
                    : chatHistory.Add(new ChatMessage(ChatRole.User, request.UserMessageText));
                logger.LogInformation("[ThreadExec] Sending {Count} messages to agent ({HistoryCount} history + 1 new): threadPath={ThreadPath}, agent={Agent}",
                    allMessages.Count, chatHistory.Count, threadPath, request.AgentName ?? "(default)");

                // 🚫 Nothing to send: no current user turn AND no prior history. There is
                // genuinely nothing for the agent to respond to — finish the round gracefully
                // WITHOUT calling the LLM. Calling it here is exactly the storm path: the chat
                // client's CreateChatClient throws ("No model selected") and AgentChatClient
                // logs it once per agent. Write the terminal state deterministically (response
                // cell → Completed, thread → Idle) and complete the round observable so the
                // submission watcher sees a settled round.
                if (allMessages.Count == 0)
                {
                    logger.LogInformation(
                        "[ThreadExec] NOTHING_TO_SEND threadPath={ThreadPath} responseId={ResponseId} — finishing round with no LLM call",
                        threadPath, responseMsgId);
                    var nothingDone = new System.Reactive.Subjects.AsyncSubject<System.Reactive.Unit>();
                    PushToResponseMessage(
                        "*Nothing to send — no message content.*",
                        ImmutableList<ToolCallEntry>.Empty, ImmutableList<NodeChangeEntry>.Empty,
                        request.AgentName, request.ModelName,
                        completedAt: DateTime.UtcNow,
                        status: ThreadMessageStatus.Completed,
                        summary: "Nothing to send.",
                        harness: request.Harness).Subscribe(
                        _ => { },
                        ex => execLogger?.LogWarning(ex,
                            "PushToResponseMessage(NothingToSend) failed for {ThreadPath}", threadPath));
                    UpdateThreadExecution(t => t.ResetExecution() with { Summary = "Nothing to send." }).Subscribe(
                        _ => { },
                        ex =>
                        {
                            execLogger?.LogWarning(ex,
                                "UpdateThreadExecution(NothingToSend): stream.Update failed for {ThreadPath}", threadPath);
                            nothingDone.OnError(ex);
                        },
                        () =>
                        {
                            nothingDone.OnNext(System.Reactive.Unit.Default);
                            nothingDone.OnCompleted();
                        });
                    return nothingDone;
                }

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
                // Guard against a disposal race: the round may already have completed and
                // disposed its CTS (or another disposal path raced here), in which case
                // Cancel() throws ObjectDisposedException — which, unhandled in
                // HandleShutdownCore, faults the whole hub teardown. A cancel of an
                // already-finished round is a no-op; swallow it.
                hub.RegisterForDisposal(_ =>
                {
                    try { executionCts.Cancel(); }
                    catch (ObjectDisposedException) { /* round already finished + CTS disposed — nothing to cancel */ }
                });

                // 🚦 Cancellation is driven by the DURABLE "cancellation requested" state, PER ROUND
                // — not a timing-fragile external watcher. Subscribe to THIS thread's own node stream
                // (which replays the current value) and cancel the round's CTS the instant
                // RequestedStatus == Cancelled is seen. A cancel requested in the CTS-storage window,
                // or re-asserted across a RESUME (where InstallCancellationWatcher's
                // (ExecutionStartedAt, HasCts) dedup swallows it — the round stays Executing with
                // RequestedStatus=Cancelled forever, the Cancel_WithPendingMessages stuck-round red),
                // is honored immediately. Set up SYNCHRONOUSLY here (before the async pool launch) so
                // there is no window; disposed with the round in the finally below. cts.Cancel() is
                // idempotent, so this is a robust complement to the watcher's sub-thread propagation.
                var cancelOnRequestSub = parentHub.GetWorkspace().GetMeshNodeStream()
                    .Select(n => n.ContentAs<MeshThread>(parentHub.JsonSerializerOptions)?.RequestedStatus)
                    .Where(rs => rs == ThreadExecutionStatus.Cancelled)
                    .Take(1)
                    .Subscribe(_ =>
                    {
                        // #321 ack — stamp "stopping…" the INSTANT the cancel is observed, from THIS
                        // hub-stream gate, via stream.Update on the own node, BEFORE tripping the CTS.
                        // Ordering is the whole fix: because the ack Update is enqueued here — before
                        // executionCts.Cancel() propagates to the streaming-loop catch that writes the
                        // terminal Cancelled — the owning hub's serialized write queue applies the ack
                        // strictly BEFORE the terminal clear. The ack and the teardown were previously two
                        // racing writers in two different contexts (InstallCancellationWatcher on the
                        // stream-emission thread vs the catch on the AI I/O pool), so under load the
                        // terminal could win and the guard (`IsExecuting && RequestedStatus==Cancelled`)
                        // would drop the ack — the CancellationAckTest timeout flake. One ordered writer
                        // removes the race. Guarded to still-executing so it can never clobber a terminal.
                        parentHub.GetWorkspace().GetMeshNodeStream().Update(
                            curr => curr?.Content is MeshThread ackThread
                                    && ackThread.Status.IsExecuting()
                                    && ackThread.RequestedStatus == ThreadExecutionStatus.Cancelled
                                ? curr with { Content = ackThread with { ExecutionStatus = CancellationRequestedStatus } }
                                : curr!)
                            .Subscribe(
                                updated => execLogger?.LogDebug(
                                    "[ThreadExec] Cancellation ack {Result} for {ThreadPath}",
                                    (updated?.Content as MeshThread)?.ExecutionStatus == CancellationRequestedStatus
                                        ? "stamped" : "skipped (already terminal)",
                                    threadPath),
                                ex => execLogger?.LogDebug(ex,
                                    "[ThreadExec] Cancellation ack stamp failed for {ThreadPath}", threadPath));

                        try { executionCts.Cancel(); }
                        catch (ObjectDisposedException) { /* round already finished */ }
                    });

                // The live response-text accumulator. Wired into the round's
                // ActiveResponseSegment + capturedResponseText BEFORE the first
                // push so InboxTool.CheckInbox can split the output cell the moment
                // streaming is observable (a check_inbox that races the first
                // "Generating response..." emission still sees a non-null
                // ResponseText and splits correctly).
                var responseText = new StringBuilder();
                capturedResponseText = responseText;
                segment.ResponseText = responseText;

                // Push progress: generating. stampActivity:false — this framework
                // placeholder is NOT agent progress, so it must not set LastActivityAt
                // (else first-token latency reads as a stall to the parent heartbeat).
                PushToResponseMessage("Generating response...", ImmutableList<ToolCallEntry>.Empty,
                    ImmutableList<NodeChangeEntry>.Empty, request.AgentName, request.ModelName,
                    status: ThreadMessageStatus.Streaming, harness: request.Harness,
                    stampActivity: false);

                // 🚦 The streaming round is an async I/O leaf and MUST run on the bounded AI
                // I/O pool — NEVER Task.Run, NEVER inline on the hub turn. The pool offloads onto
                // the ThreadPool with ConfigureAwait(false)/TaskPoolScheduler discipline (so
                // continuations never bounce back to the grain scheduler the way a bare Task.Run's
                // can) AND caps concurrent rounds. The thread hub's single turn thus stays FREE to
                // answer tool-call responses, delegation callbacks, and — critically — GetData /
                // GetPermission for its OWN output cell. A blocked turn here is exactly the
                // "harness hangs after submit" wedge (GetDataRequest to {thread}/{cell} pending for
                // tens of seconds). The `await foreach` is the only async place; tool calls + cell
                // pushes inside run as observable composition.
                // 🔁 Delegation nests: a delegating round holds its slot while awaiting a sub-thread
                // round (which takes its own slot), so the Ai cap is a runaway-fan-out stop, not a
                // fine throttle (see IoPoolOptions.Ai). Unbounded fallback when no registry is wired
                // (DI-less tests) — still offloads, just no cap.
                // See Doc/Architecture/ControlledIoPooling.md → "Streaming an agent response into a cell".
                // Completes when the round's terminal Status write SETTLES: OnNext+OnCompleted when
                // the terminal write (success/cancel/error) COMMITS, OnError when the terminal write
                // itself FAILS — so the watcher's fault path (which writes the terminal state
                // deterministically; see ThreadSubmission.CommitRoundAndExecute's onError) takes over
                // instead of the round masquerading as cleanly finished while the node still says
                // Executing. AsyncSubject replays its terminal signal to a late subscriber, closing
                // the race where the write lands before `.SelectMany(_ => roundCompletion)` subscribes.
                var roundCompletion = new System.Reactive.Subjects.AsyncSubject<System.Reactive.Unit>();
                var aiPool = parentHub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Ai)
                    ?? IoPool.Unbounded;
                // poolCt (the pool's cancellation) is intentionally unused — the round's
                // own executionCts.Token (below) is authoritative and is cancelled on hub
                // disposal, so cancellation flows through it regardless of the pool token.
                return aiPool.Invoke(async poolCt =>
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

                    // #147: hard wall-clock cap at the I/O boundary. Link to executionCts so a
                    // user Stop / hub disposal still cancels immediately; CancelAfter adds the
                    // MaxStreamingDuration ceiling on top. An unbounded external network wait was
                    // the primary wedge defect (an LLM stream that never yielded another update
                    // parked the continuation forever) — with the cap, the continuation ALWAYS
                    // resumes. A timeout-induced cancellation is surfaced as a graceful round
                    // ERROR (converted to TimeoutException below → the generic catch writes
                    // Status=Error to the response cell); the requested-cancel path
                    // (executionCts) keeps its Cancelled semantics. Disposed in the finally,
                    // before executionCts.
                    var maxStreamingDuration =
                        parentHub.ServiceProvider.GetService<AiStreamingLimits>()?.MaxStreamingDuration
                        ?? MaxStreamingDuration;
                    var streamingTimeoutCts =
                        CancellationTokenSource.CreateLinkedTokenSource(executionCts.Token);
                    streamingTimeoutCts.CancelAfter(maxStreamingDuration);
                    var ct = streamingTimeoutCts.Token;
                    // responseText / capturedResponseText / segment.ResponseText were
                    // wired just before the first push (above) so a check_inbox
                    // racing the round start still sees the live accumulator.
                    int? inputTokens = null;
                    int? outputTokens = null;
                    int? totalTokens = null;
                    // Prompt-cache breakdown — SUBSETS of inputTokens (see UsageTokens). Accumulated
                    // from UsageDetails.AdditionalCounts across rounds so the counter and the per-model
                    // TokenUsage satellite reflect the real (billable) cached context, not just the
                    // non-cached delta the raw "in" number used to show.
                    int? cacheReadTokens = null;
                    int? cacheWriteTokens = null;
                    // Total-token normalization is the static NormalizeTotal helper (below),
                    // assigned per terminal path — NOT a local function here. A mutable-capturing
                    // local function threaded through this ~1400-line method's branches exploded
                    // Roslyn's nullable-flow/closure analysis: the MeshWeaver.AI ~10-min compile
                    // cliff (build step 259s → 676s at e30e9b5f1). See NormalizeTotal.
                    // Actual model the harness reports using (e.g. Claude Code resolves
                    // "sonnet" → a concrete id). Captured from the response updates so
                    // the output cell records what really ran, not just what was asked.
                    string? actualModel = null;

                    // Cancellation sources, in order of intent:
                    //   • Stop button / delegation cancel / hub disposal → executionCts →
                    //     graceful Cancelled (the filtered catch below).
                    //   • MaxStreamingDuration wall-clock cap → streamingTimeoutCts alone →
                    //     graceful ERROR (the generic catch below). The cap is generous
                    //     enough that a legitimate delegation tree completes well within it;
                    //     only a genuinely-hung endpoint exceeds it (#147). Sub-thread
                    //     staleness within the cap remains the HeartbeatTicker's job.

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

                    // 💓 #321 reasoning heartbeat. Derived from the SAME `snapshots` stream:
                    // every real emission (text chunk / tool call / result) restarts an idle
                    // timer via Switch, so a heartbeat only fires during a genuine gap — the
                    // stretches where the model reasons between (or during) tool calls and the
                    // Sample(100ms) path is silent. Each tick stamps "Reasoning… (Ns)" onto the
                    // thread's ExecutionStatus (otherwise null for the whole round, so this is
                    // strictly additive — no existing writer competes). The write is guarded
                    // inside the Update lambda to the still-Executing, not-cancel-requested state
                    // so it never clobbers the cancellation ack or a terminal status, and the
                    // subscription is disposed with the round via `using` on every exit path
                    // (normal completion, requested cancel, timeout, error) — bounded and
                    // self-cancelling, no hand-rolled Task/Timer/SemaphoreSlim.
                    using var heartbeatSub = snapshots
                        .Select(_ => 0L)
                        .StartWith(0L)
                        .Select(_ => Observable.Timer(HeartbeatIdleThreshold, HeartbeatIdleThreshold))
                        .Switch()
                        .Subscribe(ticks =>
                        {
                            if (ct.IsCancellationRequested)
                                return;
                            var elapsed = (int)((ticks + 1) * HeartbeatIdleThreshold.TotalSeconds);
                            var heartbeatStatus = $"Reasoning… ({elapsed}s)";
                            // Re-seed identity before the cross-thread cell/thread write — same
                            // reason as PushToResponseMessage (the timer callback runs on a pool
                            // thread where AsyncLocal Context was dropped).
                            if (userAccessContext != null)
                                parentHub.ServiceProvider.GetService<AccessService>()?.SetContext(userAccessContext);
                            parentHub.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
                                node?.Content is MeshThread t
                                    && t.Status == ThreadExecutionStatus.Executing
                                    && t.RequestedStatus != ThreadExecutionStatus.Cancelled
                                    ? node with { Content = t with { ExecutionStatus = heartbeatStatus } }
                                    : node!)
                                .Subscribe(
                                    _ => { },
                                    ex => logger.LogDebug(ex,
                                        "[ThreadExec] Reasoning heartbeat stamp failed for {Path}", threadPath));
                        });
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

                    // Pass ALL messages through the harness's client. MeshWeaver →
                    // AgentChatClient (2-arg streaming); Claude Code / Copilot → that
                    // harness's own CLI IChatClient (3-arg). Both yield ChatResponseUpdate.
                    var responseStream = harnessClient != null
                        ? harnessClient.GetStreamingResponseAsync(allMessages, options: null, ct)
                        : client.GetStreamingResponseAsync(allMessages, ct);
                    // 🚦 ConfigureAwait(false) is MANDATORY: this is the ONLY await in the
                    // round-streaming lambda, and it drives PushToResponseMessage + the
                    // terminal-Status write that signals round completion. Without it, each
                    // MoveNextAsync resumes on whatever scheduler was captured — and the agent
                    // stream's inner awaits (real LLM I/O, or a fake client's await Task.Delay)
                    // can complete on a per-node HUB action-block thread. The round body would
                    // then resume on that single-threaded hub scheduler, which under a 2-core
                    // runner is busy/parked → the continuation is queued but never pumped → the
                    // round never completes → the submission watcher never observes completion →
                    // the whole round is a MISSED OBSERVATION (all threads parked, no app frame).
                    // ConfigureAwait(false) pins the iteration to the ThreadPool (the IoPool's
                    // domain) so completion is never gated on a hub scheduler. This is the
                    // "await only in the IoPool" rule: the streaming await must never capture and
                    // resume on a hub/grain context.
                    await foreach (var update in responseStream.ConfigureAwait(false))
            {
                // Diagnostic: surface every content-kind we see. If FunctionInvokingChatClient
                // eats the FunctionCallContent before we see it, this loop only logs TextContent /
                // UsageContent — the smoking gun for "toolCalls=0" failures.
                if (update.Contents.Count > 0)
                {
                    logger.LogDebug("[ThreadExec] STREAM_UPDATE kinds=[{Kinds}]",
                        string.Join(",", update.Contents.Select(c => c.GetType().Name)));
                }

                // Record the actual model the harness used (last non-empty wins).
                if (!string.IsNullOrEmpty(update.ModelId))
                    actualModel = update.ModelId;

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
                        // Prompt-cache breakdown — provider-agnostic (OpenAI/OpenRouter put the cached
                        // subset in AdditionalCounts["InputTokenDetails.CachedTokenCount"]; the Claude
                        // client normalises to the same shape). Subsets of InputTokenCount; used for
                        // accurate cost + the counter's cache line. Used to be dropped entirely.
                        var (roundCacheRead, roundCacheWrite) = UsageTokens.SplitCache(d);
                        if (roundCacheRead > 0)
                            cacheReadTokens = (cacheReadTokens ?? 0) + (int)roundCacheRead;
                        if (roundCacheWrite > 0)
                            cacheWriteTokens = (cacheWriteTokens ?? 0) + (int)roundCacheWrite;
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
                    totalTokens = NormalizeTotal(totalTokens, inputTokens, outputTokens);
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
                        request.AgentName, actualModel ?? request.ModelName,
                        inputTokens: inputTokens, outputTokens: outputTokens,
                        totalTokens: totalTokens, completedAt: DateTime.UtcNow,
                        status: ThreadMessageStatus.Completed,
                        summary: summaryText, harness: request.Harness,
                        cacheReadTokens: cacheReadTokens, cacheWriteTokens: cacheWriteTokens).Subscribe(
                        _ => { },
                        ex => execLogger?.LogWarning(ex,
                            "PushToResponseMessage(Completed) failed for {ThreadPath}", threadPath));
                    // Clear streaming state AND publish the dedicated Summary
                    // in the SAME stream.Update cycle as the Status → Idle
                    // flip. Single emission → the parent's reactive subscriber
                    // (DelegationTool) sees both Summary and Idle atomically,
                    // never reads a stale empty Summary in an interleaving.
                    // Token usage is NOT stored on the thread — record it onto the per-model
                    // TokenUsage satellite ({threadPath}/_Usage/{model}); all cost tracking lives
                    // outside the Thread node now.
                    // 🚨 The satellite write is an INDEPENDENT subscribed side effect — it must NOT
                    // gate the terminal Status write or the round-completion gate. The satellite is a
                    // SEPARATE node; the GUI chip and the token tests WAIT for it (a Where(...).Timeout
                    // read), so it can land shortly AFTER the round shows terminal. Chaining it BEFORE
                    // the Idle flip (via SelectMany) delayed the terminal write on token-reporting
                    // rounds — up to RecordUsage's 15s cap — and gated roundCompletion on that write,
                    // so a slow satellite create+accumulate under load could push the round past the
                    // delegation tests' terminal-status timeouts. RecordUsage is fail-open (never
                    // errors) and a guaranteed no-op on zero-token rounds, so this fire-and-subscribe
                    // never touches the round on the no-usage path.
                    TokenUsageNodeType.RecordUsage(parentHub, threadPath,
                        AgentPickerProjection.PartitionOf(threadPath),
                        actualModel ?? request.ModelName, inputTokens, outputTokens, execLogger,
                        cacheReadTokens, cacheWriteTokens)
                    .Subscribe(
                        _ => { },
                        ex => execLogger?.LogWarning(ex,
                            "RecordUsage(Completed) failed for {ThreadPath}", threadPath));
                    // 🚨 Round-end inbox reconcile: fold the ids check_inbox drained this round
                    // (delivered inline, no node write) into THIS single terminal write — pending
                    // -= consumed, ingested ∪= consumed. Snapshot captured ONCE so the fold is
                    // deterministic across optimistic-concurrency retries of the lambda. The
                    // channel is Reset in the finally below (covers all terminal paths).
                    var completedConsumedIds = ThreadInboxChannel.For(parentHub).TakeConsumedIds();
                    UpdateThreadExecution(t => FoldConsumedInbox(t.ResetExecution() with
                    {
                        Summary = summaryText
                    }, completedConsumedIds)).Subscribe(
                        _ => { },
                        ex =>
                        {
                            execLogger?.LogWarning(ex,
                                "UpdateThreadExecution(Idle/Completed): stream.Update failed for {ThreadPath}",
                                threadPath);
                            // The terminal write FAILED — the node may still say Executing. Fault
                            // the gate so the watcher's onError writes the terminal state
                            // deterministically (no reliance on the stuck-round watchdog).
                            roundCompletion.OnError(ex);
                        },
                        () =>
                        {
                            roundCompletion.OnNext(System.Reactive.Unit.Default);
                            roundCompletion.OnCompleted();
                        });
                    // Notify parent via SubmitMessageResponse so delegation callback resolves.
                    // Must post on the _Exec hub (hub) — the SubmitMessageResponse handler
                    // is registered there and forwards to the thread hub via ResponseFor.
                    NotifyParentCompletion(parentHub, threadPath, finalText, true, aggregatedChanges);
                    EmitCompletionNotification(parentHub, threadPath, finalText, request.AgentName);
                    }
                    // Only a REQUESTED cancel (Stop button via RequestedStatus=Cancelled, delegation
                    // cancel, hub disposal — everything that fires executionCts) is a graceful
                    // Cancelled. A wall-clock streaming timeout also surfaces as
                    // OperationCanceledException (the linked streamingTimeoutCts fired WITHOUT
                    // executionCts) but must NOT masquerade as a user cancel — it falls through to
                    // the generic catch below and terminates as a round ERROR (#147).
                    catch (OperationCanceledException) when (executionCts.IsCancellationRequested)
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
                        // Record tokens consumed BEFORE the cancel — the streaming loop
                        // already aggregated any UsageContent seen prior to the
                        // OperationCanceledException — so the cell + thread reflect what
                        // the round actually cost.
                        totalTokens = NormalizeTotal(totalTokens, inputTokens, outputTokens);
                        PushToResponseMessage(cancelText, cancelToolCalls, cancelNodeChanges,
                            request.AgentName, request.ModelName,
                            inputTokens: inputTokens, outputTokens: outputTokens,
                            totalTokens: totalTokens,
                            completedAt: DateTime.UtcNow,
                            status: ThreadMessageStatus.Cancelled,
                            cacheReadTokens: cacheReadTokens, cacheWriteTokens: cacheWriteTokens).Subscribe(
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
                        // Terminal Cancelled (not Idle): a distinct, visible status.
                        // Clear the cancel request now that it's achieved; leave
                        // PendingUserMessages intact so the submission watcher
                        // re-dispatches a fresh round from Cancelled+pending.
                        // A cancelled round still cost tokens — record them on the satellite as an
                        // INDEPENDENT subscribed side effect (see Completed branch: NOT chained before
                        // the terminal write; the reader WAITS for the satellite, so it may land just
                        // after the terminal Cancelled status). Fail-open + no-op on zero tokens.
                        TokenUsageNodeType.RecordUsage(parentHub, threadPath,
                            AgentPickerProjection.PartitionOf(threadPath),
                            request.ModelName, inputTokens, outputTokens, execLogger,
                            cacheReadTokens, cacheWriteTokens)
                        .Subscribe(
                            _ => { },
                            ex => execLogger?.LogWarning(ex,
                                "RecordUsage(Cancelled) failed for {ThreadPath}", threadPath));
                        // Round-end inbox reconcile (see Completed branch): fold the consumed
                        // follow-ups into THIS terminal write. A cancelled round may still have
                        // delivered follow-ups inline via check_inbox; they're treated as ingested
                        // (not re-queued) — the user can resubmit. Reset happens in the finally.
                        var cancelledConsumedIds = ThreadInboxChannel.For(parentHub).TakeConsumedIds();
                        UpdateThreadExecution(t => FoldConsumedInbox(t with
                        {
                            Status = ThreadExecutionStatus.Cancelled, RequestedStatus = null,
                            ExecutionStatus = null, ActiveMessageId = null,
                            ExecutionStartedAt = null, StreamingText = null, StreamingToolCalls = null,
                            Summary = cancelSummary
                        }, cancelledConsumedIds)).Subscribe(
                            _ => { },
                            ex =>
                            {
                                execLogger?.LogWarning(ex,
                                    "UpdateThreadExecution(Idle/Cancelled): stream.Update failed for {ThreadPath}",
                                    threadPath);
                                // Terminal write failed → fault the gate (see Completed branch).
                                roundCompletion.OnError(ex);
                            },
                            () =>
                            {
                                roundCompletion.OnNext(System.Reactive.Unit.Default);
                                roundCompletion.OnCompleted();
                            });
                        NotifyParentCompletion(parentHub, threadPath, cancelText, false, cancelNodeChanges);
                        EmitCompletionNotification(parentHub, threadPath, "Cancelled", request.AgentName);
                    }
                    catch (Exception exRaw)
                    {
                        // #147: a wall-clock streaming-timeout cancellation (streamingTimeoutCts
                        // fired, executionCts did NOT) reaches here as OperationCanceledException.
                        // Convert it to a descriptive TimeoutException so the standard error path
                        // below (response cell → Status=Error, thread → Idle, parent notified)
                        // tells the user WHY the round was aborted instead of a bare "The
                        // operation was canceled." — never a silent swallow, never a fake cancel.
                        var ex = exRaw is OperationCanceledException
                                 && streamingTimeoutCts.IsCancellationRequested
                                 && !executionCts.IsCancellationRequested
                            ? new TimeoutException(
                                $"AI streaming exceeded the maximum round duration of " +
                                $"{maxStreamingDuration} and was aborted (#147). The model endpoint " +
                                "stopped responding mid-stream; resubmit to retry.",
                                exRaw)
                            : exRaw;
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
                        // A CLI harness that isn't logged in (e.g. Claude Code "Not logged in · Please
                        // run /login") surfaces as AuthRequiredException — render an actionable "/login"
                        // affordance instead of the cryptic "exit code 1" the SDK throws.
                        var errorText = (ex is AuthRequiredException authEx
                            ? errorTextBase + "\n\n" + authEx.ToMarkdown()
                            : errorTextBase + $"\n\n*Error: {ex.Message}*").Trim();
                        // 🚨 NO await on hub-touching observables in src/. Subscribe-
                        // continuation: push the error cell, then flip Idle, then notify.
                        // (Previous `.ToTask()` bridge would deadlock the action block —
                        // forbidden per feedback_no_totask_in_src.md / AsynchronousCalls.md.)
                        // Record tokens consumed before the fault (same rationale as the
                        // Cancelled branch) so an errored round still reports its cost.
                        totalTokens = NormalizeTotal(totalTokens, inputTokens, outputTokens);
                        var pushErrorObs = PushToResponseMessage(errorText, errorToolCalls, errorNodeChanges,
                            request.AgentName, request.ModelName,
                            inputTokens: inputTokens, outputTokens: outputTokens,
                            totalTokens: totalTokens,
                            completedAt: DateTime.UtcNow,
                            status: ThreadMessageStatus.Error,
                            cacheReadTokens: cacheReadTokens, cacheWriteTokens: cacheWriteTokens)
                            .Timeout(TimeSpan.FromSeconds(10));
                        var errorTextLocal = errorText;
                        var errorNodeChangesLocal = errorNodeChanges;
                        pushErrorObs.Subscribe(
                            _ => { },
                            pushEx =>
                            {
                                execLogger?.LogWarning(pushEx,
                                    "PushToResponseMessage(Error) failed for {ThreadPath}", threadPath);
                                // The error-cell push faulted, so the inner Idle write never runs —
                                // fault the gate so the watcher's onError writes the terminal state.
                                roundCompletion.OnError(pushEx);
                            },
                            () =>
                            {
                                // Summary invariant for the Error path — non-empty.
                                var errorSummary = string.IsNullOrEmpty(errorTextLocal)
                                    ? $"Error: {ex.Message}"
                                    : errorTextLocal;
                                // Tokens burned before the fault — record on the satellite as an
                                // INDEPENDENT subscribed side effect (see Completed branch: NOT chained
                                // before the terminal Idle write; the reader WAITS for the satellite).
                                // Fail-open + no-op on zero tokens.
                                TokenUsageNodeType.RecordUsage(parentHub, threadPath,
                                    AgentPickerProjection.PartitionOf(threadPath),
                                    request.ModelName, inputTokens, outputTokens, execLogger,
                                    cacheReadTokens, cacheWriteTokens)
                                .Subscribe(
                                    _ => { },
                                    recEx => execLogger?.LogWarning(recEx,
                                        "RecordUsage(Error) failed for {ThreadPath}", threadPath));
                                // Round-end inbox reconcile (see Completed branch): fold the
                                // consumed follow-ups into THIS terminal write so they are not
                                // double-delivered (once inline, once as a fresh round's input).
                                // Reset happens in the finally.
                                var errorConsumedIds = ThreadInboxChannel.For(parentHub).TakeConsumedIds();
                                UpdateThreadExecution(t => FoldConsumedInbox(t with
                                {
                                    Status = ThreadExecutionStatus.Idle, ExecutionStatus = null, ActiveMessageId = null,
                                    ExecutionStartedAt = null, StreamingText = null, StreamingToolCalls = null,
                                    Summary = errorSummary
                                }, errorConsumedIds)).Subscribe(
                                    _ => { },
                                    updEx =>
                                    {
                                        execLogger?.LogWarning(updEx,
                                            "UpdateThreadExecution(Idle/Error): stream.Update failed for {ThreadPath}",
                                            threadPath);
                                        // Terminal write failed → fault the gate (see Completed branch).
                                        roundCompletion.OnError(updEx);
                                    },
                                    () =>
                                    {
                                        NotifyParentCompletion(parentHub, threadPath, errorTextLocal, false, errorNodeChangesLocal);
                                        EmitCompletionNotification(parentHub, threadPath, errorTextLocal, request.AgentName);
                                        roundCompletion.OnNext(System.Reactive.Unit.Default);
                                        roundCompletion.OnCompleted();
                                    });
                            });
                    }
                    finally
                    {
                        delegationStampSub?.Dispose();
                        cancelOnRequestSub.Dispose();
                        // Dispose the per-round CLI harness client (Claude Code / Copilot).
                        // The cached AgentChatClient (MeshWeaver path) is never disposed
                        // here — it's reused across rounds.
                        if (harnessClient is IDisposable sd) sd.Dispose();
                        else if (harnessClient is IAsyncDisposable sad) _ = sad.DisposeAsync();
                        // Detach the accumulator so a check_inbox call between
                        // rounds can't split on a dead StringBuilder (the guard
                        // also requires IsExecuting + matching ActiveMessageId).
                        segment.ResponseText = null;
                        // 🚨 Round-end: clear ALL per-round inbox channel state — the consumed
                        // ids were folded into the terminal node write above; any offered-but-not-
                        // consumed messages remain in PendingUserMessages (the watcher dispatches a
                        // next round for them) and are dropped from the queue so they re-offer
                        // cleanly. Runs for Completed/Cancelled/Error alike (this finally covers
                        // every terminal path); the fold lambdas captured their consumed snapshot
                        // BEFORE this, so Reset here cannot lose a fold.
                        ThreadInboxChannel.For(parentHub).Reset();
                        parentHub.Set<CancellationTokenSource>(null!);
                        // Linked timeout source first (unregisters from executionCts), then the
                        // round CTS itself. Both catch blocks above read IsCancellationRequested
                        // BEFORE this finally runs, so disposal never races the classification.
                        streamingTimeoutCts.Dispose();
                        executionCts.Dispose();
                        // No per-_Exec stream handle to dispose — writes went through
                        // IMeshNodeStreamCache.Update, whose upstream handle is owned
                        // by the cache and outlives this round.
                    }
                    return System.Reactive.Unit.Default;
                })
                // Gate completion on the terminal Status write LANDING (roundCompletion fires from
                // each terminal path's UpdateThreadExecution), then surface real faults to the caller
                // (the submission watcher) via OnError instead of swallowing them. Disposal-race
                // exceptions during teardown stay swallowed.
                .SelectMany(_ => roundCompletion)
                .Catch<System.Reactive.Unit, Exception>(streamingEx =>
                    {
                        var disposalRace = streamingEx is ObjectDisposedException
                            || (streamingEx is InvalidOperationException ioe
                                && ioe.Message.Contains("disposed", StringComparison.OrdinalIgnoreCase));
                        if (disposalRace)
                            return Observable.Empty<System.Reactive.Unit>();
                        logger.LogError(streamingEx,
                            "[ThreadExec] streaming round faulted for {ThreadPath}", threadPath);
                        return Observable.Throw<System.Reactive.Unit>(streamingEx);
                    });
                    }); // end of LoadFullConversationHistory.SelectMany
                }); // end of contextNodeObs.SelectMany
                })
                // Agent-init stall/error: recover (unstick the UI + stamp the cell) and COMPLETE
                // the round without faulting the chain — the watcher treats an init-stall as a
                // settled (Idle) round, exactly as the prior void method did (it never told the
                // watcher). A Timeout/init error short-circuits the SelectMany above and lands here.
                .Catch<System.Reactive.Unit, Exception>(ex =>
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

                    // Re-seed the user AccessContext before the unstick writes. This recovery path runs
                    // on the Catch continuation thread where the AsyncLocal is gone; its cross-hub
                    // stream.Update + UpdateResponseCell would otherwise post UpdateStreamRequest with no
                    // AccessContext and fail closed — leaving the thread stuck Executing (the very state
                    // this handler exists to clear).
                    if (userAccessContext != null)
                        accessService?.SetContext(userAccessContext);

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
                    UpdateResponseCell(parentHub, responsePath, threadPath, responseMsgId,
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
                    return Observable.Empty<System.Reactive.Unit>();
                });
        }); // end of clientObs.SelectMany
        }); // end of requestObs.SelectMany (resume-selection recovery)
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
    /// <see cref="SatelliteTableMapping"/>) and shows up
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
        // The thread lives under its owner's partition ({owner}/_Thread/{id}); the owner is the
        // recipient whose delivery preferences (bell/email for the ChatReady category) apply.
        var owner = threadPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        NotificationService.Dispatch(
            hub,
            recipient: owner,
            mainNodePath: threadPath,
            title: $"\"{threadName}\" is ready",
            message: preview,
            type: NotificationType.ChatReady,
            targetNodePath: threadPath,
            // agentName arrives as a full PATH (resolution form); store the friendly short name.
            createdBy: SelectionId.IdOf(agentName) ?? "agent",
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
    /// Normalizes a round's total token count: providers vary — some report a total, others
    /// only in/out. Returns <paramref name="total"/> when present, else in+out (when either is
    /// present), else null. STATIC on purpose — NOT a local function inside ExecuteMessageAsync:
    /// a mutable-capturing local function threaded through that ~1400-line reactive method's
    /// branches exploded Roslyn's nullable-flow/closure analysis and was the MeshWeaver.AI
    /// ~10-minute compile cliff (build step 259s → 676s at commit e30e9b5f1).
    /// </summary>
    internal static int? NormalizeTotal(int? total, int? inputTokens, int? outputTokens)
        => total ?? ((inputTokens.HasValue || outputTokens.HasValue)
            ? (inputTokens ?? 0) + (outputTokens ?? 0)
            : (int?)null);

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
    /// Stream-update cancellation: clients set <see cref="MeshThread.RequestedStatus"/>
    /// = <c>Cancelled</c> on the thread node via
    /// <c>workspace.GetMeshNodeStream(threadPath).Update(...)</c>. The watcher
    /// below observes the OWN thread node, treats every transition to
    /// "<c>RequestedStatus == Cancelled</c> while executing" as a cancel signal, and
    /// propagates that request onto every active delegation sub-thread. The round's OWN CTS
    /// is cancelled by its per-round RequestedStatus self-cancel (see <c>ExecuteMessageAsync</c>),
    /// not here; this watcher only handles sub-thread propagation and the claim-window No-CTS
    /// fallback.
    ///
    /// <para>Dedup is by <see cref="MeshThread.ExecutionStartedAt"/>: each round
    /// has a distinct start timestamp, so <c>DistinctUntilChanged</c> on it acts
    /// at most once per round. After the CTS is cancelled the streaming loop's
    /// <c>catch</c> writes the terminal <c>Status = Cancelled, RequestedStatus = null</c>,
    /// at which point the <c>IsExecuting</c> filter stops matching. A subsequent
    /// round (new <c>ExecutionStartedAt</c>) re-arms the watcher.</para>
    ///
    /// <para><b>No-CTS fallback.</b> If the request lands during the claim window
    /// (<c>StartingExecution</c>, before the streaming loop stored its CTS) there
    /// is no loop to write the terminal status — the watcher writes it directly,
    /// guarded so it only fires while still <c>StartingExecution</c>.</para>
    /// </summary>
    private static void InstallCancellationWatcher(IMessageHub hub)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        var threadPath = hub.Address.Path;

        var sub = hub.GetWorkspace().GetMeshNodeStream()
            // ContentAs (not `Content is`): a degraded JsonElement own-node would silently fail
            // the type test → the cancel watcher (and the No-CTS claim-window fallback) goes
            // inert and a user cancel never settles the round. ContentAs logs the degrade loud.
            .Select(n => (Node: n, Thread: n.ContentAs<MeshThread>(hub.JsonSerializerOptions, logger)))
            .Where(x => x.Thread is { RequestedStatus: ThreadExecutionStatus.Cancelled, IsExecuting: true })
            // Dedup per round — distinct ExecutionStartedAt, so the cancel is handled at most
            // once per round. The round's OWN cancellation is now driven by its per-round
            // RequestedStatus self-cancel (ExecuteMessageAsync), which replays the current value
            // and so is robust across the CTS-storage window AND a resume — so this watcher no
            // longer needs to re-arm on CTS availability (the old (ExecutionStartedAt, HasCts)
            // key). It only (a) propagates the cancel to sub-threads and (b) covers the pure
            // claim-window case (Status stuck at StartingExecution) via the No-CTS fallback below.
            .DistinctUntilChanged(x => x.Thread!.ExecutionStartedAt)
            .Subscribe(
                x =>
                {
                    var thread = x.Thread!;

                    // #321 ack — the "stopping…" stamp lives in ExecuteMessageAsync's per-round
                    // self-cancel gate (which fires executionCts.Cancel()), NOT here. That is the
                    // ONE ordered writer: it enqueues the ack Update before it trips the CTS, so the
                    // owning hub's serialized queue applies the ack before the streaming-loop catch's
                    // terminal Cancelled. Stamping it here too would re-introduce the two-context race
                    // (this watcher runs on the stream-emission thread; the terminal write on the AI
                    // I/O pool) that dropped the ack under load — the CancellationAckTest flake. This
                    // watcher now only (a) propagates the cancel to sub-threads and (b) covers the
                    // pure claim-window case via the No-CTS fallback below.

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
                                ? curr with { Content = sub with { RequestedStatus = ThreadExecutionStatus.Cancelled } }
                                : curr!)
                            .Subscribe(_ => { }, ex => logger?.LogWarning(ex,
                                "[ThreadExec] Cancel propagation failed for {SubThread}", subPath));
                    }

                    // The round's OWN CTS is cancelled by its per-round RequestedStatus
                    // self-cancel (ExecuteMessageAsync) — robust across the CTS-storage window and
                    // a resume — so no cts.Cancel() is needed here. The ONLY case the self-cancel
                    // can't cover is a cancel that lands in the CLAIM window (StartingExecution,
                    // before the round and its self-cancel exist): there is no streaming loop to
                    // write the terminal status, so write it directly — guarded to fire only while
                    // still StartingExecution with the cancel still requested, so we never clobber a
                    // round that has since reached Executing (its loop owns the terminal write).
                    if (hub.Get<CancellationTokenSource>() is null)
                    {
                        logger?.LogDebug(
                            "[ThreadExec] Cancel: no CTS for {ThreadPath} (claim window) — writing terminal Cancelled directly",
                            threadPath);
                        hub.GetWorkspace().GetMeshNodeStream().Update(
                            curr => curr?.Content is MeshThread t
                                    && t.Status == ThreadExecutionStatus.StartingExecution
                                    && t.RequestedStatus == ThreadExecutionStatus.Cancelled
                                ? curr with
                                {
                                    Content = t with
                                    {
                                        Status = ThreadExecutionStatus.Cancelled,
                                        RequestedStatus = null,
                                        ActiveMessageId = null,
                                        ExecutionStartedAt = null,
                                        ExecutionStatus = null,
                                    }
                                }
                                : curr!)
                            .Subscribe(_ => { }, ex => logger?.LogWarning(ex,
                                "[ThreadExec] No-CTS cancel fallback failed for {ThreadPath}", threadPath));
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
        return hub.GetMeshNodeStream(threadPath)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            // ContentAs (not `Content as`): a degraded JsonElement thread node LOGS instead
            // of silently failing the cast → an empty history submitted as a "secret" degrade.
            .Select(threadNode => threadNode.ContentAs<MeshThread>(hub.JsonSerializerOptions, logger))
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
                        hub.GetMeshNodeStream($"{threadPath}/{id}")
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
        return hub.GetMeshNodeStream(threadPath)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            // ContentAs (not `Content as`): a degraded JsonElement thread node LOGS instead
            // of silently failing the cast → an empty prior-message set on a "secret" degrade.
            .Select(threadNode => threadNode.ContentAs<MeshThread>(hub.JsonSerializerOptions, logger))
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
                    hub.GetMeshNodeStream($"{threadPath}/{id}")
                        .Take(1)
                        .Timeout(TimeSpan.FromSeconds(5))
                        .Select(n => (MeshNode?)n)
                        .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null)));

                return Observable.CombineLatest(cellLookups)
                    .Take(1)
                    .Select(nodes => (IReadOnlyList<ThreadMessage>)nodes
                        .Where(n => n != null)
                        .Select(n => n!.ContentAs<ThreadMessage>(hub.JsonSerializerOptions))
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
