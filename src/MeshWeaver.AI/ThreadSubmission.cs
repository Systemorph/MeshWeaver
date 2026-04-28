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
    /// </summary>
    public static RoundDispatch? PlanNextRound(MeshThread thread)
    {
        if (thread.IsExecuting) return null;
        var unprocessed = FindUnprocessedUserMessages(thread);
        if (unprocessed.IsEmpty) return null;

        var responseMessageId = Guid.NewGuid().ToString("N")[..8];
        return new RoundDispatch(
            unprocessed,
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
        var fallbackPath = threadNode.Path!;

        var delivery = ctx.Hub.Post(
            new CreateNodeRequest(threadNode),
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

                    var createdNode = cnr.Node ?? threadNode;
                    var createdPath = createdNode.Path ?? fallbackPath;
                    ctx.OnThreadCreated?.Invoke(createdNode);

                    var append = ctx.Hub.Post(
                        new AppendUserMessageRequest
                        {
                            ThreadPath = createdPath,
                            UserMessageId = Guid.NewGuid().ToString("N")[..8],
                            UserText = ctx.UserText,
                            AgentName = ctx.AgentName,
                            ModelName = ctx.ModelName,
                            ContextPath = ctx.ContextPath,
                            Attachments = ctx.Attachments
                        },
                        o => o.WithTarget(new Address(createdPath)));

                    if (append != null)
                    {
                        ctx.Hub.Observe((IMessageDelivery)append)
                            .Subscribe(
                                appendResp =>
                                {
                                    if (appendResp.Message is AppendUserMessageResponse { Success: false } fail)
                                        ctx.OnError?.Invoke($"Append after thread create failed: {fail.Error ?? "unknown"}");
                                },
                                ex => ctx.OnError?.Invoke($"Append after thread create failed: {ex.Message}"));
                    }
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
        try
        {
            var msg = ThreadInput.CreateUserMessage(
                req.UserText,
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
            hub.Post(new AppendUserMessageResponse { Success = true }, o => o.ResponseFor(delivery));
        }
        catch (Exception ex)
        {
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
        meshService.CreateNode(errorCell).Subscribe(
            _ =>
            {
                workspace.UpdateMeshNode(node =>
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
                });

                hub.Post(new AppendUserMessageResponse { Success = true }, o => o.ResponseFor(delivery));
            },
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

        hub.GetWorkspace().UpdateMeshNode(node =>
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
        });
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
/// One execution round to dispatch. Includes every unprocessed user message id
/// (batched ingestion) and the newly allocated output cell id.
/// </summary>
public sealed record RoundDispatch(
    ImmutableList<string> UserMessageIds,
    string ResponseMessageId,
    string? AgentName,
    string? ModelName,
    string? ContextPath,
    IReadOnlyList<string>? Attachments);

/// <summary>
/// Server-side watcher: observes thread state changes and dispatches execution rounds.
/// Installed once on thread hub initialization. Non-blocking; uses only Post + RegisterCallback
/// and workspace stream subscriptions.
/// </summary>
internal static class ThreadSubmissionServer
{
    public static IDisposable InstallServerWatcher(IMessageHub threadHub)
    {
        var logger = threadHub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        var workspace = threadHub.GetWorkspace();
        var threadPath = threadHub.Address.Path;

        // Reentrancy guard: 0=idle, 1=dispatching.
        // Held until IsExecuting=true is observed back through the same stream, so a
        // re-emission triggered by our own response-cell write or PendingUserMessages
        // patch can't double-dispatch.
        var dispatching = 0;

        // Subscribe to the thread's MeshNodeReference stream. Note: the stream emits on
        // ANY MeshNode write in this hub's collection (thread node, satellite cells, ...)
        // because ReduceToMeshNode returns the last-updated node from the collection.
        // We MUST filter to thread-node emissions BEFORE throttling — otherwise a
        // satellite-cell write arriving within 50ms of a thread-state commit will shadow
        // the commit (Throttle keeps only the last) and the watcher never sees the state
        // change. That was the resubmit-truncation flake: ApplyResubmit posts an
        // UpdateNodeRequest for the replayed user cell *and* commits the truncation;
        // the cell emission landed last, the watcher saw Content=ThreadMessage, skipped,
        // and the dispatch never fired until the next unrelated write.
        //
        // Throttle still sits after the filter so rapid AppendUserMessageRequest patches
        // coalesce into a single dispatch with all the queued user ids in one round.
        var sub = workspace.GetStream(new MeshNodeReference())
            ?.Where(change => change.Value?.Content is MeshThread)
            ?.Throttle(TimeSpan.FromMilliseconds(50))
            ?.Subscribe(change =>
            {
                var threadNode = change.Value;
                if (threadNode?.Content is not MeshThread thread) return;

                // Identity assertion at the watcher tick — this is where the loss showed
                // up in the Orleans delegation tests. The watcher subscription was
                // installed during hub init at a point when AsyncLocal AccessContext
                // had NOT yet been set by SetThreadHubIdentity (separate Subscribe
                // branch), so Throttle's timer-scheduler delivers ticks with the
                // captured-at-install context — not the user's identity. We expect
                // the user identity (ideally thread.CreatedBy) here; log loudly when
                // it's missing or wrong so the cascade isn't silent.
                var accessService = threadHub.ServiceProvider.GetService<AccessService>();
                var asyncLocalAtTick = accessService?.Context?.ObjectId;
                var circuitAtTick = accessService?.CircuitContext?.ObjectId;
                if (asyncLocalAtTick != thread.CreatedBy)
                {
                    logger?.LogWarning(
                        "[ThreadSubmission] watcher tick IDENTITY_MISMATCH thread={ThreadPath} " +
                        "asyncLocal={AsyncLocal} circuit={Circuit} expected={CreatedBy} — " +
                        "AsyncLocal was lost across the GetStream Subscribe boundary; " +
                        "DispatchRound will fall back to thread.CreatedBy.",
                        threadPath, asyncLocalAtTick ?? "(null)",
                        circuitAtTick ?? "(null)",
                        thread.CreatedBy ?? "(null)");
                }

                logger?.LogDebug(
                    "[ThreadSubmission] watcher tick thread={ThreadPath} IsExecuting={IsExecuting} " +
                    "Messages=[{Messages}] Ingested=[{Ingested}] UserIds=[{UserIds}] dispatching={Dispatching}",
                    threadPath, thread.IsExecuting,
                    string.Join(",", thread.Messages),
                    string.Join(",", thread.IngestedMessageIds),
                    string.Join(",", thread.UserMessageIds),
                    dispatching);

                // IsExecuting=true is visible — we held the guard waiting for this commit.
                if (thread.IsExecuting && dispatching == 1)
                {
                    Interlocked.Exchange(ref dispatching, 0);
                    return;
                }
                if (thread.IsExecuting) return;

                if (Interlocked.CompareExchange(ref dispatching, 1, 0) != 0)
                {
                    logger?.LogDebug(
                        "[ThreadSubmission] watcher skip thread={ThreadPath} — dispatching already 1",
                        threadPath);
                    return;
                }

                var releaseGuard = true;
                try
                {
                    var dispatch = ThreadSubmission.PlanNextRound(thread);
                    if (dispatch is null)
                    {
                        logger?.LogDebug(
                            "[ThreadSubmission] watcher idle thread={ThreadPath} — nothing to dispatch",
                            threadPath);
                        return;
                    }

                    logger?.LogDebug(
                        "[ThreadSubmission] watcher dispatching thread={ThreadPath} userIds=[{UserIds}] responseId={ResponseId}",
                        threadPath, string.Join(",", dispatch.UserMessageIds), dispatch.ResponseMessageId);

                    // Hold the guard. It will be released when we observe IsExecuting=true
                    // back on this same stream above (or on hard failure inside DispatchRound).
                    releaseGuard = false;
                    DispatchRound(threadHub, threadNode, dispatch, logger,
                        onFailure: () => Interlocked.Exchange(ref dispatching, 0));
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "[ThreadSubmission] Server watcher iteration failed for {ThreadPath}", threadPath);
                }
                finally
                {
                    if (releaseGuard) Interlocked.Exchange(ref dispatching, 0);
                }
            });

        return sub ?? System.Reactive.Disposables.Disposable.Empty;
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
        var userCtx = asyncLocalCtx ?? circuitCtx;
        var fellBackToCreatedBy = false;
        if (userCtx is null && !string.IsNullOrEmpty(thread.CreatedBy))
        {
            userCtx = new AccessContext { ObjectId = thread.CreatedBy, Name = thread.CreatedBy };
            fellBackToCreatedBy = true;
        }

        // Identity-trace at the dispatch boundary. The watcher callback runs after
        // Throttle(50ms) on a timer scheduler — AsyncLocal context from the original
        // delivery is gone here, so we expect asyncLocal=null and fall back to either
        // the persistent circuit context (Blazor) or thread.CreatedBy (Orleans).
        logger?.LogInformation(
            "[ThreadSubmission] DispatchRound identity thread={ThreadPath} responseId={ResponseId} " +
            "asyncLocal={AsyncLocal} circuit={Circuit} createdBy={CreatedBy} fallbackToCreatedBy={FallbackToCreatedBy} effective={Effective}",
            threadPath, responseMsgId,
            asyncLocalCtx?.ObjectId ?? "(null)",
            circuitCtx?.ObjectId ?? "(null)",
            thread.CreatedBy ?? "(null)",
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

        var combinedUserText = pendingForRound.Count > 0
            ? string.Join("\n\n---\n\n", pendingForRound.Select(p => p.Msg.Text))
            : (thread.PendingUserMessage ?? "");

        void AfterUserCellsReady()
        {
            // Step 1: create the assistant output cell (CreateNodeRequest → RegisterCallback).
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
                    ModelName = dispatch.ModelName
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
                        hub.GetWorkspace().UpdateMeshNode(node =>
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
                        });

                        hub.Post(
                            new UpdateThreadMessageContent { Text = "Allocating agent..." },
                            o => o.WithTarget(new Address(responsePath)));

                        // Step 3: post to _Exec hosted hub — actual agent streaming runs there.
                        var executionHub = hub.GetHostedHub(
                            new Address($"{hub.Address}/_Exec"),
                            config => config.WithHandler<SubmitMessageRequest>(ThreadExecution.ExecuteMessageAsync),
                            HostedHubCreation.Always);

                        executionHub!.Post(
                            new SubmitMessageRequest
                            {
                                ThreadPath = threadPath,
                                UserMessageText = combinedUserText,
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
        var creationStreams = pendingForRound.Select(p =>
        {
            var cell = new MeshNode(p.Id, threadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType,
                MainNode = mainEntity,
                Content = p.Msg
            };
            return meshService.CreateNode(cell)
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
