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
public static class ThreadSubmission
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
    /// Returns <c>null</c> when the thread is currently executing or has no queued user messages.
    ///
    /// <para>One queued user message per round (Claude-Code-style turn structure):
    /// each user submission gets its own response cell + its own agent turn. After
    /// a turn completes (success/cancel/error), <c>IsExecuting</c> flips back to
    /// false and the watcher fires again to dispatch the next queued message.
    /// Multi-message batching with "---" joiners is gone.</para>
    /// </summary>
    public static RoundDispatch? PlanNextRound(MeshThread thread)
    {
        if (thread.IsExecuting) return null;
        var unprocessed = FindUnprocessedUserMessages(thread);
        if (unprocessed.IsEmpty) return null;

        var nextId = unprocessed[0];
        var responseMessageId = Guid.NewGuid().ToString("N")[..8];
        return new RoundDispatch(
            ImmutableList.Create(nextId),
            responseMessageId,
            thread.PendingAgentName,
            thread.PendingModelName,
            thread.PendingContextPath,
            thread.PendingAttachments);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Client-side API — invoked from Blazor click handlers (void, non-blocking)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Submits a user message into an existing thread. Posts a single
    /// <see cref="AppendUserMessageRequest"/> to the thread hub — the handler
    /// runs <see cref="ThreadInput.AppendUserInput"/> locally (one atomic
    /// <c>workspace.UpdateMeshNode</c>), and the server watcher then creates the
    /// satellite cell and dispatches the round. No separate CreateNodeRequest from
    /// the client — that was the duplicate-dispatch source in the legacy flow.
    /// </summary>
    public static void Submit(SubmitContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.ThreadPath))
        {
            ctx.OnError?.Invoke("Submit requires ThreadPath. Use CreateThreadAndSubmit for new threads.");
            return;
        }

        var delivery = ctx.Hub.Post(
            new AppendUserMessageRequest
            {
                ThreadPath = ctx.ThreadPath!,
                UserMessageId = Guid.NewGuid().ToString("N")[..8], // ignored by handler — kept for back-compat shape
                UserText = ctx.UserText,
                AgentName = ctx.AgentName,
                ModelName = ctx.ModelName,
                ContextPath = ctx.ContextPath,
                Attachments = ctx.Attachments
            },
            o => o.WithTarget(new Address(ctx.ThreadPath!)));

        if (delivery == null)
        {
            ctx.OnError?.Invoke("Hub.Post returned null");
            return;
        }

        ctx.Hub.Observe((IMessageDelivery)delivery)
            .Subscribe(
                response =>
                {
                    if (response.Message is AppendUserMessageResponse { Success: false } fail)
                        ctx.OnError?.Invoke($"Submit failed: {fail.Error ?? "unknown"}");
                },
                ex => ctx.OnError?.Invoke($"Submit failed: {ex.Message}"));
    }

    /// <summary>
    /// Creates a new thread node, then submits the first user message via
    /// <see cref="AppendUserMessageRequest"/> on the new thread.
    /// <see cref="SubmitContext.OnThreadCreated"/> fires when the thread is confirmed.
    /// </summary>
    public static void CreateThreadAndSubmit(SubmitContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.Namespace))
        {
            ctx.OnError?.Invoke("CreateThreadAndSubmit requires Namespace.");
            return;
        }

        var threadNode = ThreadNodeType.BuildThreadNode(ctx.Namespace!, ctx.UserText, ctx.CreatedBy);

        // Bundle the first user message into the thread create itself. The mesh hub
        // forwards Argument to the new thread hub fire-and-forget after persistence —
        // one round-trip end-to-end instead of CreateNodeRequest then AppendUserMessage.
        var initialAppend = new AppendUserMessageRequest
        {
            ThreadPath = threadNode.Path!,
            UserMessageId = Guid.NewGuid().ToString("N")[..8],
            UserText = ctx.UserText,
            AgentName = ctx.AgentName,
            ModelName = ctx.ModelName,
            ContextPath = ctx.ContextPath,
            Attachments = ctx.Attachments
        };

        var delivery = ctx.Hub.Post(
            new CreateNodeRequest(threadNode) { Argument = initialAppend },
            o => o.WithTarget(new Address(ctx.Namespace!)));

        if (delivery == null)
        {
            ctx.OnError?.Invoke("Hub.Post returned null");
            return;
        }

        ctx.Hub.Observe((IMessageDelivery)delivery)
            .Subscribe(
                response =>
                {
                    if (response.Message is not CreateNodeResponse { Success: true } cnr)
                    {
                        var err = (response.Message as CreateNodeResponse)?.Error ?? "unknown";
                        ctx.OnError?.Invoke($"Thread creation failed: {err}");
                        return;
                    }

                    ctx.OnThreadCreated?.Invoke(cnr.Node ?? threadNode);
                },
                ex => ctx.OnError?.Invoke($"Thread creation failed: {ex.Message}"));
    }

    /// <summary>
    /// Resubmits an existing user message: truncates <c>Messages</c> and <c>IngestedMessageIds</c>
    /// after the replayed id, optionally updating the user cell text. The server watcher
    /// creates a new output cell.
    /// </summary>
    public static void Resubmit(ResubmitContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.ThreadPath) || string.IsNullOrEmpty(ctx.UserMessageIdToReplay))
        {
            ctx.OnError?.Invoke("Resubmit requires ThreadPath and UserMessageIdToReplay.");
            return;
        }

        var delivery = ctx.Hub.Post(
            new ResubmitUserMessageRequest
            {
                ThreadPath = ctx.ThreadPath,
                UserMessageId = ctx.UserMessageIdToReplay,
                NewUserText = ctx.NewUserText,
                AgentName = ctx.AgentName,
                ModelName = ctx.ModelName
            },
            o => o.WithTarget(new Address(ctx.ThreadPath)));

        if (delivery == null)
        {
            ctx.OnError?.Invoke("Hub.Post returned null");
            return;
        }

        ctx.Hub.Observe((IMessageDelivery)delivery)
            .Subscribe(
                response =>
                {
                    if (response.Message is AppendUserMessageResponse { Success: false } fail)
                        ctx.OnError?.Invoke($"Resubmit failed: {fail.Error ?? "unknown"}");
                },
                ex => ctx.OnError?.Invoke($"Resubmit failed: {ex.Message}"));
    }

    // ═════════════════════════════════════════════════════════════════════
    // Server-side API — invoked from thread hub initialization
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Installs a continuous subscription on the thread hub's workspace.
    /// Whenever the thread is idle and has unprocessed user messages, opens a new round
    /// (creates output cell, updates Messages/Ingested/IsExecuting/Active/Pending*, posts to _Exec).
    /// </summary>
    public static IDisposable InstallServerWatcher(IMessageHub threadHub)
        => ThreadSubmissionServer.InstallServerWatcher(threadHub);

    // ═════════════════════════════════════════════════════════════════════
    // Server-side handlers for client requests
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Thread-hub handler kept as a back-compat shim: re-routes legacy
    /// <see cref="AppendUserMessageRequest"/> through the new <see cref="ThreadInput.AppendUserInput"/>
    /// path. New callers should write directly to the thread's MeshNode via ThreadInput
    /// instead of posting this request.
    /// </summary>
    public static IMessageDelivery HandleAppendUserMessage(
        IMessageHub hub,
        IMessageDelivery<AppendUserMessageRequest> delivery)
    {
        var req = delivery.Message;
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.ThreadSubmission");
        logger?.LogDebug(
            "[AppendUserMsg] handler entry hub={Hub} threadPath={ThreadPath} accessCtx={AccessCtx} textLen={TextLen} agent={Agent} model={Model}",
            hub.Address, req.ThreadPath,
            delivery.AccessContext?.ObjectId ?? "(null)",
            req.UserText?.Length ?? 0, req.AgentName ?? "(null)", req.ModelName ?? "(null)");
        try
        {
            var msg = ThreadInput.CreateUserMessage(
                req.UserText ?? string.Empty,
                createdBy: delivery.AccessContext?.ObjectId,
                authorName: null,
                agentName: req.AgentName,
                modelName: req.ModelName,
                contextPath: req.ContextPath,
                attachments: req.Attachments);
            // Note: this shim ignores req.UserMessageId — the new flow allocates its own.
            // Tests + the legacy client posted the id eagerly; the new flow only uses
            // server-allocated ids so we don't honour the request's id here.
            ThreadInput.AppendUserInput(hub.GetWorkspace(), req.ThreadPath, msg);
            logger?.LogDebug(
                "[AppendUserMsg] AppendUserInput dispatched for {ThreadPath} (cold subscribe fired)",
                req.ThreadPath);
            hub.Post(new AppendUserMessageResponse { Success = true }, o => o.ResponseFor(delivery));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "[AppendUserMsg] handler threw for {ThreadPath}", req.ThreadPath);
            hub.Post(new AppendUserMessageResponse { Success = false, Error = ex.Message }, o => o.ResponseFor(delivery));
        }
        return delivery.Processed();
    }

    /// <summary>
    /// Thread-hub handler: records a failed submission. Creates an error response cell
    /// (role=assistant, Text=ErrorMessage, marked as AgentResponse), registers the user
    /// message id on the thread if not already there, and marks it as ingested.
    /// The UI sees the natural chat flow: user message followed by an error reply.
    /// </summary>
    public static IMessageDelivery HandleRecordSubmissionFailure(
        IMessageHub hub,
        IMessageDelivery<RecordSubmissionFailureRequest> delivery)
    {
        var req = delivery.Message;
        var errorResponseId = Guid.NewGuid().ToString("N")[..8];

        // Create the error response cell at {threadPath}/{errorResponseId}.
        var errorCell = new MeshNode(errorResponseId, req.ThreadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = req.ThreadPath,
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = $"**Submission failed:** {req.ErrorMessage}",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            }
        };

        // Canonical satellite-creation pattern (see SatelliteEntityPatterns.md):
        // create the child node first, and only update the parent's Messages list +
        // post the response inside the Subscribe(onNext) callback. The previous
        // fire-and-forget hub.Post + immediate state update raced the watcher: tests
        // saw the new id on the thread before the cell was actually persisted, then
        // ReadNode at {threadPath}/{errorResponseId} returned null.
        var workspace = hub.GetWorkspace();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        // Chain CreateNode → UpdateMeshNode via SelectMany so the response is only posted
        // after the satellite cell is persisted AND the parent's state commit completes.
        // UpdateMeshNode returns a cold IObservable<MeshNode>; SelectMany subscribes it,
        // which triggers the dsStream.Update side effect. Without the SelectMany subscribe,
        // the parent state never updated.
        meshService.CreateNode(errorCell)
            .SelectMany(_ => workspace.GetMeshNodeStream().Update(node =>
            {
                var t = node.Content as MeshThread ?? new MeshThread();
                var msgs = t.Messages;
                if (!msgs.Contains(req.UserMessageId)) msgs = msgs.Add(req.UserMessageId);
                if (!msgs.Contains(errorResponseId)) msgs = msgs.Add(errorResponseId);
                var userIds = t.UserMessageIds.Contains(req.UserMessageId)
                    ? t.UserMessageIds
                    : t.UserMessageIds.Add(req.UserMessageId);
                var ingested = t.IngestedMessageIds.Contains(req.UserMessageId)
                    ? t.IngestedMessageIds
                    : t.IngestedMessageIds.Add(req.UserMessageId);
                return node with
                {
                    Content = t with
                    {
                        Messages = msgs,
                        UserMessageIds = userIds,
                        IngestedMessageIds = ingested,
                        // Clear any pending text for this message so the watcher doesn't dispatch it again.
                        PendingUserMessage = null
                    }
                };
            }))
            .Subscribe(
                _ => hub.Post(
                    new AppendUserMessageResponse { Success = true },
                    o => o.ResponseFor(delivery)),
                ex => hub.Post(
                    new AppendUserMessageResponse { Success = false, Error = ex.Message },
                    o => o.ResponseFor(delivery)));

        return delivery.Processed();
    }

    /// <summary>
    /// Thread-hub handler: truncates the thread after the replayed user message id,
    /// drops it from IngestedMessageIds, optionally updates its text, and resets the
    /// executing flags. Watcher re-dispatches.
    /// </summary>
    public static IMessageDelivery HandleResubmitUserMessage(
        IMessageHub hub,
        IMessageDelivery<ResubmitUserMessageRequest> delivery)
    {
        var req = delivery.Message;
        ApplyResubmit(hub, req.ThreadPath, req.UserMessageId, req.NewUserText, req.AgentName, req.ModelName);
        hub.Post(new AppendUserMessageResponse { Success = true }, o => o.ResponseFor(delivery));
        return delivery.Processed();
    }

    /// <summary>
    /// Truncates the thread after <paramref name="userMessageId"/>, drops it from
    /// IngestedMessageIds so the watcher re-dispatches a new round, and optionally
    /// updates the user cell text. Shared by <see cref="HandleResubmitUserMessage"/>
    /// and the legacy <see cref="ResubmitMessageRequest"/> shim.
    /// </summary>
    public static void ApplyResubmit(
        IMessageHub hub,
        string threadPath,
        string userMessageId,
        string? newUserText,
        string? agentName,
        string? modelName)
    {
        // Optionally update the user cell text.
        if (!string.IsNullOrEmpty(newUserText))
        {
            var updatedCell = new MeshNode(userMessageId, threadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType,
                Content = new ThreadMessage
                {
                    Role = "user",
                    Text = newUserText,
                    Timestamp = DateTime.UtcNow,
                    Type = ThreadMessageType.ExecutedInput
                }
            };
            hub.Post(new UpdateNodeRequest(updatedCell), o => o.WithTarget(hub.Address));
        }

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.ThreadSubmission");
        hub.GetWorkspace().GetMeshNodeStream().Update(node =>
        {
            var t = node.Content as MeshThread ?? new MeshThread();
            var idx = t.Messages.IndexOf(userMessageId);
            if (idx < 0) return node;

            var keep = t.Messages.Take(idx + 1).ToImmutableList();
            var trimmedUserIds = t.UserMessageIds.Where(uid => keep.Contains(uid)).ToImmutableList();
            var ingested = t.IngestedMessageIds.Remove(userMessageId);
            return node with
            {
                Content = t with
                {
                    Messages = keep,
                    UserMessageIds = trimmedUserIds,
                    IngestedMessageIds = ingested,
                    IsExecuting = false,
                    ActiveMessageId = null,
                    ExecutionStartedAt = null,
                    PendingUserMessage = newUserText ?? t.PendingUserMessage,
                    PendingAgentName = agentName ?? t.PendingAgentName,
                    PendingModelName = modelName ?? t.PendingModelName
                }
            };
        }).Subscribe(
            _ => { },
            ex => logger?.LogWarning(ex,
                "ApplyResubmit: UpdateMeshNode failed for thread {ThreadPath} message {MessageId}",
                threadPath, userMessageId));
    }
}

/// <summary>
/// Input for a client-side submission (existing or new thread).
/// </summary>
public sealed record SubmitContext
{
    public required IMessageHub Hub { get; init; }
    /// <summary>Target thread path. Null for <see cref="ThreadSubmission.CreateThreadAndSubmit"/>.</summary>
    public string? ThreadPath { get; init; }
    /// <summary>Parent namespace for new thread creation. Required for <see cref="ThreadSubmission.CreateThreadAndSubmit"/>.</summary>
    public string? Namespace { get; init; }
    public required string UserText { get; init; }
    public string? AgentName { get; init; }
    public string? ModelName { get; init; }
    public string? ContextPath { get; init; }
    public IReadOnlyList<string>? Attachments { get; init; }
    public string? CreatedBy { get; init; }
    public string? AuthorName { get; init; }

    /// <summary>
    /// Called exactly once if the submit fails (post returned null, timeout, permission denied).
    /// Never invoked after a successful submit.
    /// </summary>
    public Action<string>? OnError { get; init; }

    /// <summary>
    /// Called exactly once for <see cref="ThreadSubmission.CreateThreadAndSubmit"/> when the
    /// thread node is confirmed. The caller typically navigates here.
    /// </summary>
    public Action<MeshNode>? OnThreadCreated { get; init; }
}

/// <summary>
/// Input for a resubmission (truncate + re-ingest).
/// </summary>
public sealed record ResubmitContext
{
    public required IMessageHub Hub { get; init; }
    public required string ThreadPath { get; init; }
    public required string UserMessageIdToReplay { get; init; }
    /// <summary>New text for the user cell. Null means reuse the existing cell text.</summary>
    public string? NewUserText { get; init; }
    public string? AgentName { get; init; }
    public string? ModelName { get; init; }
    public Action<string>? OnError { get; init; }
}

/// <summary>
/// One execution round to dispatch. <see cref="UserMessageIds"/> contains exactly one
/// id (per <see cref="ThreadSubmission.PlanNextRound"/> — one user message per round,
/// one response cell per round, Claude-Code-style turn structure). The collection
/// shape is kept for back-compat with downstream code that already iterates it.
/// </summary>
public sealed record RoundDispatch(
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
///     <see cref="DispatchRoundObs"/> observable that creates satellite cells, commits
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
    public static IDisposable InstallServerWatcher(IMessageHub threadHub)
    {
        var logger = threadHub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        return threadHub.WatchSubmission(
            fingerprint:   Fingerprint,
            needsDispatch: NeedsDispatch,
            dispatch:      node => DispatchRoundObs(threadHub, node, logger),
            logger:        logger);
    }

    /// <summary>
    /// Compress the dispatchable state to a value tuple. Equality on this tuple
    /// drives <c>DistinctUntilChanged</c> — when nothing relevant has changed,
    /// the watcher doesn't re-dispatch.
    /// </summary>
    private static (bool, int, int, int) Fingerprint(MeshNode node)
    {
        if (node.Content is not MeshThread t) return (false, 0, 0, 0);
        return (t.IsExecuting, t.Messages.Count, t.IngestedMessageIds.Count, t.PendingUserMessages.Count);
    }

    /// <summary>
    /// True when this thread state warrants a new round: not executing AND
    /// has at least one unprocessed user id or pending message.
    /// </summary>
    private static bool NeedsDispatch(MeshNode node)
    {
        if (node.Content is not MeshThread t) return false;
        if (t.IsExecuting) return false;
        if (t.PendingUserMessages.Count > 0) return true;
        foreach (var id in t.UserMessageIds)
            if (!t.IngestedMessageIds.Contains(id)) return true;
        return false;
    }

    /// <summary>
    /// Wrap the existing <see cref="DispatchRound"/> in <c>IObservable&lt;Unit&gt;</c>
    /// so it composes via <c>Concat</c>. Each queued dispatch re-reads the LATEST
    /// thread state — the upstream emission's captured <paramref name="threadNode"/>
    /// is only used as a fallback when the workspace read fails.
    ///
    /// <para>Why re-read: WatchSubmission uses <c>Concat</c> to serialize dispatch
    /// rounds. If 3 upstream fingerprint emissions queue up faster than the first
    /// dispatch's async commit can land, each queued dispatch must re-evaluate
    /// against current state. Otherwise the captured (IsExecuting=false) state
    /// at upstream-emission time bypasses <see cref="PlanNextRound"/>'s
    /// <c>IsExecuting</c> guard and we get duplicate response cells per round
    /// (the Resubmit flake symptom).</para>
    /// </summary>
    private static IObservable<System.Reactive.Unit> DispatchRoundObs(
        IMessageHub hub, MeshNode threadNode, ILogger<AgentChatClient>? logger) =>
        System.Reactive.Linq.Observable.Defer(() =>
            hub.GetWorkspace().GetMeshNodeStream().Take(1)
                .Select(latest => latest ?? threadNode)
                .SelectMany(latest =>
                {
                    if (latest?.Content is not MeshThread thread)
                        return System.Reactive.Linq.Observable.Return(System.Reactive.Unit.Default);
                    var dispatch = ThreadSubmission.PlanNextRound(thread);
                    if (dispatch is null) // IsExecuting=true or nothing unprocessed
                        return System.Reactive.Linq.Observable.Return(System.Reactive.Unit.Default);

                    try { DispatchRound(hub, latest!, dispatch, logger, onFailure: () => { }); }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex,
                            "[ThreadSubmission] DispatchRoundObs failed for {ThreadPath}",
                            hub.Address.Path);
                        return System.Reactive.Linq.Observable.Return(System.Reactive.Unit.Default);
                    }

                    // Wait until OUR commit is visible — i.e. dispatch.ResponseMessageId
                    // appears in Thread.Messages. Waiting for `IsExecuting=true` was racey:
                    // the workspace's MeshNode cache could emit a stale IsExecuting=true
                    // from the prior round on Subscribe, completing the wait before our
                    // commit landed, freeing the single-flight guard, and letting the
                    // next dispatch fire immediately. Response id check is unique to this
                    // round so it cannot match stale state.
                    var responseId = dispatch.ResponseMessageId;
                    return hub.GetWorkspace().GetMeshNodeStream()
                        .Where(n => n?.Content is MeshThread mt && mt.Messages.Contains(responseId))
                        .Take(1)
                        .Timeout(TimeSpan.FromSeconds(10))
                        .Catch<MeshNode, Exception>(_ =>
                            System.Reactive.Linq.Observable.Return<MeshNode>(null!))
                        .Select(_ => System.Reactive.Unit.Default);
                }));

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
        Action? onFailure = null)
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
        // Only ids present in dispatch.UserMessageIds AND PendingUserMessages need creation
        // here — legacy paths (PendingUserMessage string) create cells elsewhere.
        var pendingForRound = dispatch.UserMessageIds
            .Where(id => thread.PendingUserMessages.ContainsKey(id))
            .Select(id => (Id: id, Msg: thread.PendingUserMessages[id]))
            .ToImmutableList();

        // Single-message round (PlanNextRound returns one id at a time). pendingForRound
        // is empty only on the legacy auto-execute-on-creation path that uses the
        // singular `thread.PendingUserMessage` string instead of the dictionary.
        var roundUserText = pendingForRound.Count > 0
            ? pendingForRound[0].Msg.Text
            : (thread.PendingUserMessage ?? "");

        void AfterUserCellsReady()
        {
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

                        // Step 2: commit the round to the thread state (one atomic UpdateMeshNode).
                        // Both the user satellite cells (created above in the materialization step)
                        // and the response satellite cell (just confirmed in the CreateNodeRequest
                        // callback above) exist on the hub now. Only NOW do we add their ids into
                        // Messages — the GUI iterates Messages to render LayoutAreaControls, so
                        // every id it sees has a backing satellite.
                        //
                        // The IsExecuting check is the idempotency guard — every other watcher
                        // emission in this round skips, so this body runs exactly once per round.
                        //
                        // Subscribe is mandatory: GetMeshNodeStream().Update returns a cold
                        // IObservable<MeshNode>; the dsStream.Update side effect only runs on
                        // Subscribe. The downstream UpdateResponseCell + SubmitMessageRequest
                        // chain off the Subscribe(onNext) so they only fire after the round
                        // commit is persisted.
                        hub.GetWorkspace().GetMeshNodeStream().Update(node =>
                        {
                            var t = node.Content as MeshThread ?? new MeshThread();
                            if (t.IsExecuting) return node;

                            // User ids in dispatch order, then the response id last.
                            // Contains check covers the resubmit case where u1 was already in
                            // Messages from a prior round — ApplyResubmit removed u1 from
                            // IngestedMessageIds (so the watcher re-dispatches it) but kept it
                            // in Messages, so a blind AddRange would duplicate it.
                            var msgs = t.Messages;
                            foreach (var uid in dispatch.UserMessageIds)
                                if (!msgs.Contains(uid)) msgs = msgs.Add(uid);
                            msgs = msgs.Add(responseMsgId);

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
                                    IsExecuting = true,
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
                                ThreadExecution.UpdateResponseCell(
                                    hub.GetWorkspace(), responsePath, threadPath, responseMsgId, mainEntity,
                                    msg => msg with { Text = "Allocating agent...", Status = ThreadMessageStatus.Streaming },
                                    logger);

                                // Step 3: post to _Exec hosted hub — actual agent streaming runs there.
                                var executionHub = hub.GetHostedHub(
                                    new Address($"{hub.Address}/_Exec"),
                                    config => config.WithHandler<SubmitMessageRequest>(ThreadExecution.ExecuteMessageAsync),
                                    HostedHubCreation.Always);

                                executionHub!.Post(
                                    new SubmitMessageRequest
                                    {
                                        ThreadPath = threadPath,
                                        UserMessageText = roundUserText,
                                        UserMessageId = dispatch.UserMessageIds.LastOrDefault(),
                                        ResponseMessageId = responseMsgId,
                                        ResponsePath = responsePath,
                                        AgentName = dispatch.AgentName,
                                        ModelName = dispatch.ModelName,
                                        ContextPath = dispatch.ContextPath,
                                        Attachments = dispatch.Attachments
                                    },
                                    o => userCtx != null ? o.WithAccessContext(userCtx) : o);
                            },
                            ex =>
                            {
                                logger?.LogWarning(ex,
                                    "[ThreadSubmission] Round commit UpdateMeshNode failed for {ResponseMsgId} on {ThreadPath}",
                                    responseMsgId, threadPath);
                                onFailure?.Invoke();
                            });
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
