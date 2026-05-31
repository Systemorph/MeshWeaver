using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI;

/// <summary>
/// All business logic for client-side chat submission and server-side round dispatch.
/// Single source of truth — Blazor view and the thread hub both delegate here.
///
/// Design:
/// - Client methods are void / fire-and-forget. The caller observes confirmation and
///   progress through the thread's existing MeshNode remote stream (UI already
///   subscribes for rendering) — no events, no callbacks for "processing started".
/// - Server watcher ingests ALL unprocessed user messages into a single round;
///   batched ingestion keeps one output cell per round.
/// - Pure helpers <see cref="FindUnprocessedUserMessages"/> and <see cref="PlanNextRound"/>
///   are the unit-testable core.
/// - Hard rule: no await, no IMeshService.QueryAsync, no ObserveQuery, no client
///   SubmitMessageRequest. Only Hub.Post + RegisterCallback + workspace stream writes.
/// </summary>
internal static class ThreadSubmission
{
    // ═════════════════════════════════════════════════════════════════════
    // Pure helpers — unit-test surface
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns user-message ids from <c>thread.UserMessageIds</c> that are not in
    /// <c>thread.IngestedMessageIds</c>, in original order.
    /// Empty if all user messages have already been ingested.
    /// </summary>
    public static ImmutableList<string> FindUnprocessedUserMessages(MeshThread thread)
    {
        var ingested = thread.IngestedMessageIds;
        var result = ImmutableList.CreateBuilder<string>();
        foreach (var id in thread.UserMessageIds)
        {
            if (!ingested.Contains(id))
                result.Add(id);
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// Returns the next round to dispatch given the current thread state.
    /// Returns <c>null</c> when the thread is currently executing or has nothing
    /// queued.
    ///
    /// <para><b>Inbox semantics.</b> Every entry in
    /// <see cref="MeshThread.PendingUserMessages"/> is ingested into a single
    /// round — the inbox drains the whole queue at once, all drained ids move
    /// into <see cref="MeshThread.Messages"/>, and exactly one response cell
    /// is allocated for the round. Multiple inputs share one response cell;
    /// the agent treats the drained list as a multi-message turn.</para>
    /// </summary>
    public static RoundDispatch? PlanNextRound(MeshThread thread)
    {
        // Allow planning when the thread is idle / cancelled (a stopped round
        // re-dispatches like Idle if input is queued) OR has just been claimed
        // by InstallServerWatcher (Status==StartingExecution). Reject the active
        // phase (Executing) — it owns the in-flight round.
        if (thread.Status is not (ThreadExecutionStatus.Idle
            or ThreadExecutionStatus.Cancelled
            or ThreadExecutionStatus.StartingExecution))
            return null;
        if (thread.PendingUserMessages.IsEmpty) return null;

        // Drain the entire pending queue into one round. Order follows
        // UserMessageIds (submission order); orphan pending entries not yet in
        // UserMessageIds are appended at the end (defensive, shouldn't happen).
        var idsBuilder = ImmutableList.CreateBuilder<string>();
        foreach (var id in thread.UserMessageIds)
            if (thread.PendingUserMessages.ContainsKey(id) && !idsBuilder.Contains(id))
                idsBuilder.Add(id);
        foreach (var id in thread.PendingUserMessages.Keys)
            if (!idsBuilder.Contains(id))
                idsBuilder.Add(id);
        var ids = idsBuilder.ToImmutable();
        if (ids.IsEmpty) return null;

        // 🚨 Deterministic per-claim response cell id — NOT a fresh Guid.
        // The submission claim Status oscillates (StartingExecution → rollback →
        // Idle → re-claim, and Executing → StartingExecution resume bounce), so the
        // _Exec round watcher fires DispatchAfterClaim several times for ONE logical
        // round. A fresh Guid each call minted a NEW response cell per fire →
        // duplicate cells (Thread.Messages[1] flip-flop) + abandoned execution
        // subscriptions that outlived the per-class fixture (the catastrophic
        // LifetimeScope ObjectDisposedException cascade). Deriving the id from the
        // round's drained user ids + current Messages.Count makes every re-dispatch
        // of the SAME claim resolve to the SAME cell (idempotent create/commit),
        // while a genuinely new round (next turn / resubmit, Messages.Count advanced)
        // gets a distinct cell.
        var responseMessageId = DeriveDeterministicResponseId(ids, thread.Messages.Count);
        return new RoundDispatch(
            ids,
            responseMessageId,
            thread.PendingAgentName,
            thread.PendingModelName,
            thread.PendingContextPath,
            thread.PendingAttachments);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Server-side API — invoked from thread hub initialization
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Installs a continuous subscription on the thread hub's workspace.
    /// Whenever the thread is idle and has unprocessed user messages, opens a new round.
    /// </summary>
    public static IDisposable InstallServerWatcher(IMessageHub threadHub)
        => ThreadSubmissionServer.InstallServerWatcher(threadHub);

    /// <summary>
    /// Stable 8-hex-char response cell id for a round, derived from the drained
    /// user-message ids and the thread's current message count. Deterministic so
    /// repeated dispatches of the same claim (status oscillation) reuse one cell;
    /// distinct across rounds because either the user ids or Messages.Count differ.
    /// </summary>
    internal static string DeriveDeterministicResponseId(IReadOnlyList<string> ids, int messageCount)
    {
        var key = string.Join(",", ids) + "|" + messageCount;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Legacy client-side API DELETED 2026-05-27 — use HubThreadExtensions instead:
//
//   hub.StartThread(...)              replaces  ThreadSubmission.CreateThreadAndSubmit
//   hub.SubmitMessage(...)            replaces  ThreadSubmission.Submit
//   hub.ResubmitMessage(...)          replaces  ThreadSubmission.Resubmit / ApplyResubmit
//   hub.DeleteFromMessage(...)        replaces  ThreadSubmission.ApplyDeleteFromMessage
//   hub.MarkThreadDone(...)           replaces  ThreadSubmission.MarkThreadDone
//   hub.RecordSubmissionFailure(...)  replaces  ThreadSubmission.ApplyRecordSubmissionFailure
//
// SubmitContext and ResubmitContext parameter-bag records also deleted.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// One execution round to dispatch. <see cref="UserMessageIds"/> contains exactly one
/// id (per <see cref="ThreadSubmission.PlanNextRound"/> — one user message per round,
/// one response cell per round, Claude-Code-style turn structure). The collection
/// shape is kept for back-compat with downstream code that already iterates it.
/// </summary>
internal sealed record RoundDispatch(
    ImmutableList<string> UserMessageIds,
    string ResponseMessageId,
    string? AgentName,
    string? ModelName,
    string? ContextPath,
    IReadOnlyList<string>? Attachments);

/// <summary>
/// Server-side watcher: reactively dispatches an execution round whenever the thread
/// has unprocessed user messages and isn't already running. Pure observable composition
/// via <see cref="ActivityControlPlaneExtensions.WatchSubmission{TFingerprint}"/>:
///
/// <list type="number">
///   <item><description>Source: <c>workspace.GetMeshNodeStream()</c>.</description></item>
///   <item><description><c>DistinctUntilChanged</c> on a fingerprint of
///     (IsExecuting, Messages.Count, IngestedMessageIds.Count, PendingUserMessages.Count)
///     so the same dispatchable state cannot fire twice.</description></item>
///   <item><description><c>Where</c>: not currently executing AND has at least one
///     unprocessed user id or pending message.</description></item>
///   <item><description><c>SelectMany</c>: each dispatchable emission produces a single
///     <see cref="DispatchRound"/> observable that creates satellite cells, commits
///     the round to the thread node, and posts to the <c>_Exec</c> hub.</description></item>
/// </list>
///
/// <para>No <c>Throttle</c>, no reentrancy flag, no scheduler-hop identity workarounds —
/// the source observable is the thread's own MeshNode stream and the chain runs in
/// the hub's natural scheduler. The previous imperative implementation (200 lines with
/// a <c>dispatching</c> flag + 50 ms Throttle + AsyncLocal fallbacks) is gone.</para>
/// </summary>
internal static class ThreadSubmissionServer
{
    /// <summary>
    /// Subscribes to the thread's OWN node stream and, whenever it observes
    /// <c>Status == Idle</c> with non-empty <see cref="MeshThread.PendingUserMessages"/>,
    /// runs the atomic claim (<c>Status: Idle → StartingExecution</c>) directly
    /// against the same stream. The <c>_Exec</c> hosted hub's round watcher
    /// observes the resulting transition (via the shared
    /// <see cref="IMeshNodeStreamCache"/>) and continues with Step B + C
    /// (drain pending into <see cref="MeshThread.Messages"/>, allocate response
    /// cell, flip <c>Status → Executing</c>, stream).
    ///
    /// <para>Single-flight is guaranteed by the atomic claim Update: the
    /// hub's action block serialises concurrent emissions, so the first
    /// lambda that sees <c>Status == Idle</c> flips it; every other lambda
    /// re-reads <c>Status != Idle</c> inside the predicate and bails. No
    /// in-memory <c>Interlocked</c> gate, no separate intent field, no
    /// cross-hub trigger Post.</para>
    /// </summary>
    public static IDisposable InstallServerWatcher(IMessageHub threadHub)
    {
        var logger = threadHub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        var threadPath = threadHub.Address.Path;
        // 🚨 Identity note: the claim Update below is an OWN write on the thread
        // hub — it doesn't cross a hub boundary, doesn't post a PatchDataRequest,
        // doesn't pass through any RLS gate. The action block serialises and the
        // owning hub IS the writer; ambient AsyncLocal at the Subscribe callback
        // doesn't matter for this specific transition. The real identity-needing
        // work happens in `ExecRoundWatcher` (DispatchAfterClaim creates satellite
        // cells and posts cross-hub messages) — see ThreadExecution.cs where the
        // FromNode scope is applied.
        return threadHub.GetWorkspace().GetMeshNodeStream()
            .Do(n =>
            {
                if (n?.Content is MeshThread t)
                {
                    logger?.LogDebug(
                        "[SubmissionWatcher] OBSERVE {ThreadPath} status={Status} pending={Pending} ingested={Ingested} userIds={UserIds}",
                        threadPath, t.Status, t.PendingUserMessages.Count,
                        t.IngestedMessageIds.Count, t.UserMessageIds.Count);
                }
            })
            .Select(NeedsDispatch)
            .Where(needs => needs)
            .Subscribe(
                _ =>
                {
                    logger?.LogDebug("[SubmissionWatcher] DISPATCH_TRIGGERED for {ThreadPath}", threadPath);
                    var workspace = threadHub.GetWorkspace();
                    workspace.GetMeshNodeStream().Update(node =>
                    {
                        var t = node.Content as MeshThread;
                        if (t is null
                            || t.Status is not (ThreadExecutionStatus.Idle or ThreadExecutionStatus.Cancelled)
                            || t.PendingUserMessages.IsEmpty)
                        {
                            logger?.LogDebug(
                                "[SubmissionWatcher] CLAIM_SKIPPED {ThreadPath} status={Status} pending={Pending} (re-check inside lambda)",
                                threadPath, t?.Status, t?.PendingUserMessages.Count);
                            return node; // already running or no longer pending
                        }
                        logger?.LogInformation(
                            "[SubmissionWatcher] CLAIMED: {ThreadPath} pending={Pending} → Status=StartingExecution",
                            threadPath, t.PendingUserMessages.Count);
                        // 🚨 The claim lambda MUST be deterministic on its input — concurrent
                        // emissions can result in multiple lambdas running with the same
                        // pre-update snapshot; if the resulting node is byte-identical, the
                        // downstream SynchronizationStream.SetCurrent's value-equality check
                        // dedupes the second commit (no second emission, no second dispatch).
                        // Don't stamp DateTime.UtcNow here — DispatchRound sets
                        // ExecutionStartedAt as part of its Executing-state commit, and that
                        // path runs serially on the action block via the _Exec round watcher.
                        return node with
                        {
                            Content = t with
                            {
                                Status = ThreadExecutionStatus.StartingExecution
                            }
                        };
                    }).Subscribe(
                        _ => { /* _Exec's InstallExecRoundWatcher sees Status=StartingExecution and dispatches */ },
                        ex => logger?.LogWarning(ex,
                            "[SubmissionWatcher] claim Update failed for {ThreadPath}", threadPath));
                },
                ex => logger?.LogWarning(ex,
                    "[SubmissionWatcher] stream errored for {ThreadPath}",
                    threadPath));
    }

    /// <summary>
    /// Predicate equivalent: the thread is idle and has pending work. Used by
    /// the submission watcher to filter dispatchable emissions. The lambda
    /// inside <c>Update</c> re-checks the same condition so concurrent
    /// emissions still single-flight.
    /// </summary>
    private static bool NeedsDispatch(MeshNode? node)
    {
        if (node?.Content is not MeshThread t) return false;
        // Idle OR Cancelled (a stopped round re-dispatches like Idle) with
        // queued input → claim a fresh round.
        return t.Status is ThreadExecutionStatus.Idle or ThreadExecutionStatus.Cancelled
               && t.PendingUserMessages.Count > 0;
    }


    /// <summary>
    /// Step B + Step C of the round, called from the <c>_Exec</c> hub's
    /// round watcher after observing the parent thread's <c>Status</c>
    /// transition to <see cref="ThreadExecutionStatus.StartingExecution"/>.
    /// Drains all pending entries into <see cref="MeshThread.Messages"/>,
    /// materialises user satellite cells, allocates a single response cell,
    /// transitions <see cref="ThreadExecutionStatus.StartingExecution"/> →
    /// <see cref="ThreadExecutionStatus.Executing"/>, and invokes
    /// <c>ExecuteMessageAsync</c> directly on <c>_Exec</c> for streaming.
    /// </summary>
    internal static void DispatchAfterClaim(
        IMessageHub hub, MeshNode threadNode, ILogger<AgentChatClient>? logger,
        Action? onFailure = null)
    {
        var thread = threadNode.Content as MeshThread;
        if (thread is null)
        {
            logger?.LogWarning(
                "[DispatchAfterClaim] thread node has no MeshThread content for {Path}",
                hub.Address.Path);
            onFailure?.Invoke();
            return;
        }
        var dispatch = ThreadSubmission.PlanNextRound(thread);
        if (dispatch is null)
        {
            // RESUME: an interrupted Executing round (InitializeThreadLifecycle
            // re-entered StartingExecution) has no NEW pending input but still
            // owns a response cell. Re-dispatch into that SAME cell rather than
            // rolling back — the user's question already streamed a partial
            // answer; we resume generating it.
            if (thread.Status == ThreadExecutionStatus.StartingExecution
                && !string.IsNullOrEmpty(thread.ActiveMessageId))
            {
                logger?.LogInformation(
                    "[DispatchAfterClaim] resuming interrupted round {ResponseId} for {Path}",
                    thread.ActiveMessageId, hub.Address.Path);
                var resumeDispatch = new RoundDispatch(
                    ImmutableList<string>.Empty,
                    thread.ActiveMessageId!,
                    thread.PendingAgentName,
                    thread.PendingModelName,
                    thread.PendingContextPath,
                    thread.PendingAttachments);
                DispatchRound(hub, threadNode, resumeDispatch, logger, onFailure, isResume: true);
                return;
            }

            logger?.LogDebug(
                "[DispatchAfterClaim] nothing to dispatch (post-claim race?) for {Path} — rolling status back to Idle",
                hub.Address.Path);
            // Roll the claim back so the next watcher tick can re-trigger.
            // Rollback writes the thread node — `hub` here is parentHub (the
            // thread hub), so its own GetMeshNodeStream is the OWN handle.
            hub.GetWorkspace().GetMeshNodeStream().Update(n =>
            {
                var t = n.Content as MeshThread ?? new MeshThread();
                return n with { Content = t with { Status = ThreadExecutionStatus.Idle, ExecutionStartedAt = null } };
            }).Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "[DispatchAfterClaim] rollback Update failed for {Path}", hub.Address.Path));
            return;
        }
        DispatchRound(hub, threadNode, dispatch, logger, onFailure);
    }

    /// <summary>
    /// Creates the output cell, writes the committed round to the thread node, and
    /// fires off agent execution on the _Exec hosted hub. Non-blocking — all
    /// Hub.Post + RegisterCallback; the workspace write is a synchronous fire-and-forget.
    ///
    /// Step 0 (new): for each unprocessed user id present in <see cref="MeshThread.PendingUserMessages"/>,
    /// create the satellite ThreadMessage cell. The client only writes the thread node;
    /// the server materializes the per-message satellite nodes here.
    /// </summary>
    private static void DispatchRound(
        IMessageHub hub,
        MeshNode threadNode,
        RoundDispatch dispatch,
        ILogger<AgentChatClient>? logger,
        Action? onFailure = null,
        bool isResume = false)
    {
        var threadPath = hub.Address.Path;
        var responseMsgId = dispatch.ResponseMessageId;
        var responsePath = $"{threadPath}/{responseMsgId}";
        var thread = threadNode.Content as MeshThread ?? new MeshThread();
        var mainEntity = threadNode.MainNode ?? dispatch.ContextPath ?? threadPath;

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var asyncLocalCtx = accessService?.Context;
        var circuitCtx = accessService?.CircuitContext;

        // The AsyncLocal at this point may be the THREAD HUB's own address — the
        // watcher fires on a Throttle timer scheduler and captures whatever
        // ExecutionContext was active at the time `Subscribe` was called (hub init,
        // when SetContext hadn't yet propagated). Treat hub-as-user as no-identity
        // and fall through to the wrapping MeshNode.CreatedBy (set by the
        // CreateNodeRequest handler from the requester's AccessContext).
        var hubAsUserMatch = asyncLocalCtx?.ObjectId is { } id
            && (string.Equals(id, threadPath, StringComparison.Ordinal)
                || string.Equals(id, hub.Address.ToFullString(), StringComparison.Ordinal));
        var userCtx = hubAsUserMatch ? null : (asyncLocalCtx ?? circuitCtx);

        var fellBackToCreatedBy = false;
        // Resolution: thread content's CreatedBy → wrapping node's CreatedBy → null.
        var resolvedCreatedBy = !string.IsNullOrEmpty(thread.CreatedBy)
            ? thread.CreatedBy
            : threadNode.CreatedBy;
        if (userCtx is null && !string.IsNullOrEmpty(resolvedCreatedBy))
        {
            userCtx = new AccessContext { ObjectId = resolvedCreatedBy, Name = resolvedCreatedBy };
            fellBackToCreatedBy = true;
        }

        // Identity-trace at the dispatch boundary. The watcher callback runs after
        // Throttle(50ms) on a timer scheduler — AsyncLocal context from the original
        // delivery is gone here, so we expect asyncLocal=null and fall back to either
        // the persistent circuit context (Blazor) or thread.CreatedBy (Orleans).
        logger?.LogInformation(
            "[ThreadSubmission] DispatchRound identity thread={ThreadPath} responseId={ResponseId} " +
            "asyncLocal={AsyncLocal} hubAsUserMatch={HubAsUser} circuit={Circuit} threadCreatedBy={ThreadCreatedBy} " +
            "nodeCreatedBy={NodeCreatedBy} fallbackToCreatedBy={FallbackToCreatedBy} effective={Effective}",
            threadPath, responseMsgId,
            asyncLocalCtx?.ObjectId ?? "(null)",
            hubAsUserMatch,
            circuitCtx?.ObjectId ?? "(null)",
            thread.CreatedBy ?? "(null)",
            threadNode.CreatedBy ?? "(null)",
            fellBackToCreatedBy,
            userCtx?.ObjectId ?? "(null)");

        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        // Step 0: materialize user satellite cells from PendingUserMessages.
        // dispatch.UserMessageIds is the full set the inbox drains this round
        // (PlanNextRound returns every entry). Each cell will be created below
        // and committed to Messages atomically with the response cell.
        var pendingForRound = dispatch.UserMessageIds
            .Where(id => thread.PendingUserMessages.ContainsKey(id))
            .Select(id => (Id: id, Msg: thread.PendingUserMessages[id]))
            .ToImmutableList();

        // The "current" user input fed to the agent is the LAST drained message —
        // earlier drained messages already exist as user cells in Messages and
        // load via LoadFullConversationHistory (with the last one excluded via
        // SubmitMessageRequest.UserMessageId). Multi-message round: agent sees
        // history's user cells consecutively, then this last one as the
        // current turn.
        var roundUserText = pendingForRound.Count > 0
            ? pendingForRound[^1].Msg.Text
            : (thread.PendingUserMessage ?? "");

        // Step 2 + 3: commit the round to the thread state (one atomic
        // UpdateMeshNode) and start agent streaming. Shared by the fresh-dispatch
        // path (after the response cell is created) and the resume path (cell
        // already exists). On a fresh round dispatch.UserMessageIds drains the
        // pending queue into Messages; on resume it is empty (the round's user
        // cells were ingested before the interruption) so the Add/AddRange/Remove
        // steps are all no-ops and only the StartingExecution → Executing flip +
        // ActiveMessageId re-stamp take effect.
        void CommitRoundAndExecute()
        {
            // The IsExecuting check is the idempotency guard — every other watcher
            // emission in this round skips, so this body runs exactly once per round.
            //
            // Subscribe is mandatory: cache.Update returns a cold
            // IObservable<MeshNode>; the side effect only runs on
            // Subscribe. The downstream UpdateResponseCell +
            // ExecuteMessageAsync chain off the Subscribe(onNext)
            // so they only fire after the round commit is persisted.
            // DispatchRound runs in parentHub context (hub = thread
            // hub). Write through THIS hub's own node stream so
            // sender = thread hub, AccessContext flows from the
            // caller's identity.
            // 🚨 Single-fire guard for the side effect, NOT just the state mutation.
            // The Status check below makes the UpdateMeshNode lambda a no-op on every
            // watcher re-emission after the first — but no-op Updates still call
            // OnNext (see feedback_setcurrent_skips_noops: UpdateRemote completes
            // inline with OnNext(current)). So without this flag the Subscribe(onNext)
            // body re-runs ExecuteMessageAsync on EVERY thread-node change during the
            // round (response-cell alloc, heartbeat stamp, streaming writes) → the
            // 6×-duplicate-execution bug (OrleansNodeChangePropagation: the Create
            // node-change lands in a later duplicate round's nodeChangeLog while an
            // earlier round's completion writes the cell → UpdatedNodes=[]; also the
            // AutoExecute text-empty + delegation-timeout reds). Only the emission
            // that actually performed StartingExecution→Executing launches execution.
            var didCommitThisEmission = false;
            hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
            {
                var t = node.Content as MeshThread ?? new MeshThread();
                // We expect Status==StartingExecution (post-claim from InstallServerWatcher
                // or InitializeThreadLifecycle's resume re-entry).
                // Anything else is an out-of-band state change — drop the commit.
                // Reset-then-set so an optimistic-concurrency retry of this lambda
                // reflects the FINAL decision: only a genuine StartingExecution→Executing
                // transition leaves the flag true.
                if (t.Status != ThreadExecutionStatus.StartingExecution) { didCommitThisEmission = false; return node; }
                didCommitThisEmission = true;

                // User ids in dispatch order, then the response id last.
                // Contains check covers the resubmit case where u1 was already in
                // Messages from a prior round — ApplyResubmit removed u1 from
                // IngestedMessageIds (so the watcher re-dispatches it) but kept it
                // in Messages, so a blind AddRange would duplicate it. Symmetric
                // Contains check on responseMsgId catches resume (the cell is
                // already in Messages) and DispatchRound retries.
                var msgs = t.Messages;
                foreach (var uid in dispatch.UserMessageIds)
                    if (!msgs.Contains(uid)) msgs = msgs.Add(uid);
                if (!msgs.Contains(responseMsgId)) msgs = msgs.Add(responseMsgId);

                var ingested = t.IngestedMessageIds.AddRange(
                    dispatch.UserMessageIds.Where(uid => !t.IngestedMessageIds.Contains(uid)));

                // Drop consumed PendingUserMessages entries — their satellites now exist
                // and their ids are now in Messages.
                var pending = t.PendingUserMessages;
                foreach (var (uid, _) in pendingForRound)
                    pending = pending.Remove(uid);

                return node with
                {
                    Content = t with
                    {
                        Messages = msgs,
                        IngestedMessageIds = ingested,
                        Status = ThreadExecutionStatus.Executing,
                        // ActiveMessageId is the canonical handle —
                        // full response path derives as {threadPath}/{ActiveMessageId}.
                        ActiveMessageId = responseMsgId,
                        ExecutionStartedAt = DateTime.UtcNow,
                        TokensUsed = 0,
                        ExecutionStatus = null,
                        PendingUserMessage = null,
                        PendingUserMessages = pending,
                        PendingContextPath = dispatch.ContextPath,
                        PendingAttachments = dispatch.Attachments?.ToImmutableList()
                    }
                };
            }).Subscribe(
                _ =>
                {
                    // Only the emission that flipped StartingExecution→Executing
                    // launches the round. Every other (no-op) emission's OnNext is a
                    // duplicate and must not re-enter ExecuteMessageAsync.
                    if (!didCommitThisEmission) return;

                    var allocCache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
                    ThreadExecution.UpdateResponseCell(
                        allocCache, responsePath, threadPath, responseMsgId, mainEntity,
                        msg => msg with { Text = "Allocating agent...", Status = ThreadMessageStatus.Streaming },
                        logger);

                    // Step 3: direct method call on _Exec hosted hub.
                    // No SubmitMessageRequest post — the agent loop runs as
                    // composed observables; only the LLM streaming is async.
                    var executionHub = hub.GetHostedHub(
                        new Address($"{hub.Address}/_Exec"),
                        config => config,
                        HostedHubCreation.Always);

                    ThreadExecution.ExecuteMessageAsync(executionHub!,
                        new ThreadExecution.RoundParams(
                            ThreadPath: threadPath,
                            ResponseMessageId: responseMsgId,
                            UserMessageId: dispatch.UserMessageIds.LastOrDefault(),
                            UserMessageText: roundUserText,
                            AgentName: dispatch.AgentName,
                            ModelName: dispatch.ModelName,
                            ContextPath: dispatch.ContextPath,
                            Attachments: dispatch.Attachments),
                        userCtx);
                },
                ex =>
                {
                    logger?.LogWarning(ex,
                        "[ThreadSubmission] Round commit UpdateMeshNode failed for {ResponseMsgId} on {ThreadPath}",
                        responseMsgId, threadPath);
                    onFailure?.Invoke();
                });
        }

        void AfterUserCellsReady()
        {
            if (isResume)
            {
                // Resume into the EXISTING response cell — no CreateNodeRequest.
                // Reset its streaming state (clear the partial text + tool calls
                // left by the interrupted round) and commit directly.
                var resumeCache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
                ThreadExecution.UpdateResponseCell(
                    resumeCache, responsePath, threadPath, responseMsgId, mainEntity,
                    msg => msg with
                    {
                        Text = "",
                        ToolCalls = ImmutableList<ToolCallEntry>.Empty,
                        Status = ThreadMessageStatus.Streaming,
                        CompletedAt = null
                    },
                    logger);
                CommitRoundAndExecute();
                return;
            }

            // Step 1: create the assistant output cell (CreateNodeRequest → RegisterCallback).
            // Status=Streaming until the streaming loop transitions it to Completed/Cancelled/Error.
            var responseCell = new MeshNode(responseMsgId, threadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType,
                MainNode = mainEntity,
                Content = new ThreadMessage
                {
                    Role = "assistant",
                    Text = "",
                    Timestamp = DateTime.UtcNow,
                    Type = ThreadMessageType.AgentResponse,
                    AgentName = dispatch.AgentName,
                    ModelName = dispatch.ModelName,
                    Status = ThreadMessageStatus.Streaming
                }
            };

            var createDelivery = hub.Post(
                new CreateNodeRequest(responseCell),
                o => userCtx != null
                    ? o.WithAccessContext(userCtx).WithTarget(hub.Address)
                    : o.WithTarget(hub.Address));

            if (createDelivery == null)
            {
                logger?.LogWarning("[ThreadSubmission] Post of CreateNodeRequest returned null for response cell {ResponseMsgId} on {ThreadPath}",
                    responseMsgId, threadPath);
                onFailure?.Invoke();
                return;
            }

            hub.Observe((IMessageDelivery)createDelivery)
                // The delivery observable can emit more than once for the same request
                // (Forwarded intermediate delivery + actual CreateNodeResponse, or stream
                // re-replay on resubscribe). Take exactly the first terminal response —
                // without this guard the commit step below ran 6× per Resubmit, each
                // appending the same responseMsgId to Thread.Messages.
                .Where(r => r.Message is CreateNodeResponse)
                .Take(1)
                .Subscribe(
                    response =>
                    {
                        if (response.Message is not CreateNodeResponse { Success: true })
                        {
                            var err = (response.Message as CreateNodeResponse)?.Error ?? "unknown";
                            logger?.LogWarning("[ThreadSubmission] Response cell creation failed for {ResponseMsgId} on {ThreadPath}: {Error}",
                                responseMsgId, threadPath, err);
                            onFailure?.Invoke();
                            return;
                        }

                        // Both the user satellite cells (created above in the materialization
                        // step) and the response satellite cell (just confirmed) exist on the
                        // hub now. Only NOW do we add their ids into Messages — the GUI iterates
                        // Messages to render LayoutAreaControls, so every id it sees has a
                        // backing satellite.
                        CommitRoundAndExecute();
                    },
                    ex =>
                    {
                        logger?.LogWarning(ex, "[ThreadSubmission] Response cell creation failed for {ResponseMsgId} on {ThreadPath}", responseMsgId, threadPath);
                        onFailure?.Invoke();
                    });
        }

        if (pendingForRound.Count == 0)
        {
            AfterUserCellsReady();
            return;
        }

        // Materialize satellite cells in parallel, then proceed. We swallow per-cell errors
        // (cell may already exist from a prior crashed attempt — that's recoverable) and only
        // wait for one notification per cell before continuing.
        //
        // Each CreateNodeRequest is posted via hub.Observe with explicit
        // o.WithAccessContext(userCtx) so the cell is created under the user's identity
        // (resolved from thread.CreatedBy / MeshNode.CreatedBy by DispatchRound). The
        // AsyncLocal at this watcher-callback boundary may still be the thread hub's
        // own address (Throttle scheduler hop), so meshService.CreateNode's
        // CaptureContext() would otherwise stamp deliveries with hub-as-user — leading
        // to "Node created at .../<id> by <thread-hub-path>" instead of "by <user>".
        var creationStreams = pendingForRound.Select(p =>
        {
            var cell = new MeshNode(p.Id, threadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType,
                MainNode = mainEntity,
                Content = p.Msg
            };
            return hub.Observe(new CreateNodeRequest(cell),
                    o => userCtx != null
                        ? o.WithAccessContext(userCtx).WithTarget(hub.Address)
                        : o.WithTarget(hub.Address))
                .Take(1)
                .Select(_ => true)
                .Catch<bool, Exception>(ex =>
                {
                    logger?.LogDebug(ex, "[ThreadSubmission] User cell create returned error (may already exist) for {Path}",
                        $"{threadPath}/{p.Id}");
                    return Observable.Return(true);
                });
        }).ToList();

        Observable.CombineLatest(creationStreams)
            .Take(1)
            .Subscribe(
                _ => AfterUserCellsReady(),
                ex =>
                {
                    logger?.LogWarning(ex, "[ThreadSubmission] User cell materialization failed for {ThreadPath}", threadPath);
                    onFailure?.Invoke();
                });
    }
}
