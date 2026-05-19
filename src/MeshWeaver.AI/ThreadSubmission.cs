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
        // Allow planning when the thread is fully idle OR has just been claimed
        // by HandleStartExecution (Status==StartingExecution). Reject the active
        // phases (Executing, Completing) — they own the in-flight round.
        if (thread.Status != ThreadExecutionStatus.Idle
            && thread.Status != ThreadExecutionStatus.StartingExecution)
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

        var responseMessageId = Guid.NewGuid().ToString("N")[..8];
        return new RoundDispatch(
            ids,
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
    /// Submits a user message into an existing thread. Routes through
    /// <see cref="SubmitMessageRequest"/> — the last surviving thread
    /// mutation request — because it lands on the per-thread hub in OWN
    /// context, which is where <see cref="ThreadInput.AppendUserInput"/>
    /// safely runs. A pure client-side stream.Update on the remote thread
    /// path produces duplicate writes against a stale baseline
    /// (UpdateRemote lambda re-runs per emission); SubmitMessageRequest is
    /// kept as the sanctioned trigger until that framework issue is fixed.
    /// </summary>
    public static void Submit(SubmitContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.ThreadPath))
        {
            ctx.OnError?.Invoke("Submit requires ThreadPath. Use CreateThreadAndSubmit for new threads.");
            return;
        }

        var delivery = ctx.Hub.Post(
            new SubmitMessageRequest
            {
                ThreadPath = ctx.ThreadPath!,
                UserMessageText = ctx.UserText,
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
                    if (response.Message is SubmitMessageResponse { Success: false } fail)
                        ctx.OnError?.Invoke($"Submit failed: {fail.Error ?? "unknown"}");
                },
                ex => ctx.OnError?.Invoke($"Submit failed: {ex.Message}"));
    }

    /// <summary>
    /// Creates a new thread node, then submits the first user message via
    /// <see cref="ThreadInput.AppendUserInput"/> on the new thread.
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

        // Pre-seed the thread's PendingUserMessages with the first user
        // message — single-write atomic state: when the thread hub activates,
        // its OWN MeshNode already carries the queued user input, and the
        // submission watcher dispatches without a second round-trip. No
        // separate ThreadInput.AppendUserInput needed. See RequestViaStreamUpdate.md.
        var firstMessageId = Guid.NewGuid().ToString("N")[..8];
        var firstMessage = ThreadInput.CreateUserMessage(
            ctx.UserText,
            createdBy: ctx.CreatedBy,
            authorName: ctx.AuthorName,
            agentName: ctx.AgentName,
            modelName: ctx.ModelName,
            contextPath: ctx.ContextPath,
            attachments: ctx.Attachments);

        var seededThread = (threadNode.Content as MeshThread ?? new MeshThread()) with
        {
            Messages = ImmutableList.Create(firstMessageId),
            UserMessageIds = ImmutableList.Create(firstMessageId),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                .SetItem(firstMessageId, firstMessage),
            PendingAgentName = ctx.AgentName,
            PendingModelName = ctx.ModelName,
            PendingContextPath = ctx.ContextPath,
            PendingAttachments = ctx.Attachments
        };
        threadNode = threadNode with { Content = seededThread };

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

        try
        {
            // Direct stream.Update via the shared ApplyResubmit helper — no
            // bespoke ThreadSubmission.ApplyResubmit. ApplyResubmit truncates
            // Messages/IngestedMessageIds after the replayed id, optionally
            // updates the user cell, and the server watcher re-dispatches.
            ApplyResubmit(ctx.Hub, ctx.ThreadPath, ctx.UserMessageIdToReplay,
                ctx.NewUserText, ctx.AgentName, ctx.ModelName);
        }
        catch (Exception ex)
        {
            ctx.OnError?.Invoke($"Resubmit failed: {ex.Message}");
        }
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
    // Legacy AppendUserMessage / Resubmit / RecordSubmissionFailure handlers
    // removed — see RequestViaStreamUpdate.md. Public entry points:
    //   • ThreadInput.AppendUserInput  (append a new user message)
    //   • ApplyResubmit                (truncate + re-stamp pending)
    //   • ApplyDeleteFromMessage       (truncate Messages list)
    //   • ApplyRecordSubmissionFailure (error cell + parent state)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Records a failed submission by creating an error response cell and
    /// updating the parent thread's state in one chained operation. Fans out
    /// to the OWN-update fast path when invoked from the per-thread hub
    /// itself, and to a posted <see cref="RecordSubmissionFailureTrigger"/>
    /// (handled inline on the thread hub in OWN context) when invoked from
    /// any other hub. See <c>RequestViaStreamUpdate.md</c>.
    /// </summary>
    public static void ApplyRecordSubmissionFailure(
        IMessageHub hub,
        string threadPath,
        string userMessageId,
        string userText,
        string errorMessage)
    {
        if (!string.Equals(hub.Address.Path, threadPath, StringComparison.Ordinal))
        {
            hub.Post(new RecordSubmissionFailureTrigger(threadPath, userMessageId, userText, errorMessage),
                o => o.WithTarget(new Address(threadPath)));
            return;
        }
        ApplyRecordSubmissionFailureOwn(hub, threadPath, userMessageId, userText, errorMessage);
    }

    internal static void ApplyRecordSubmissionFailureOwn(
        IMessageHub hub,
        string threadPath,
        string userMessageId,
        string userText,
        string errorMessage)
    {
        var errorResponseId = Guid.NewGuid().ToString("N")[..8];
        var errorCell = new MeshNode(errorResponseId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = threadPath,
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = $"**Submission failed:** {errorMessage}",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            }
        };
        var workspace = hub.GetWorkspace();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.ThreadSubmission");
        meshService.CreateNode(errorCell)
            .SelectMany(_ => workspace.GetMeshNodeStream().Update(node =>
            {
                var t = node.Content as MeshThread ?? new MeshThread();
                var msgs = t.Messages;
                if (!msgs.Contains(userMessageId)) msgs = msgs.Add(userMessageId);
                if (!msgs.Contains(errorResponseId)) msgs = msgs.Add(errorResponseId);
                var userIds = t.UserMessageIds.Contains(userMessageId)
                    ? t.UserMessageIds
                    : t.UserMessageIds.Add(userMessageId);
                var ingested = t.IngestedMessageIds.Contains(userMessageId)
                    ? t.IngestedMessageIds
                    : t.IngestedMessageIds.Add(userMessageId);
                return node with
                {
                    Content = t with
                    {
                        Messages = msgs,
                        UserMessageIds = userIds,
                        IngestedMessageIds = ingested,
                        // Clear pending text so the watcher doesn't dispatch it again.
                        PendingUserMessage = null
                    }
                };
            }))
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "ApplyRecordSubmissionFailure: chained update failed for thread {ThreadPath} message {MessageId}",
                    threadPath, userMessageId));
    }

    /// <summary>
    /// Handler for <see cref="StartExecutionTrigger"/>: the SOLE entry point
    /// for a new execution round.
    ///
    /// <para>Posted by <see cref="ThreadSubmissionServer.InstallServerWatcher"/>
    /// whenever it observes <c>Status == Idle</c> with non-empty
    /// <c>PendingUserMessages</c>. The handler runs in the thread hub's OWN
    /// context (action block) so the atomic claim is race-free against any
    /// concurrent <see cref="ThreadInput.AppendUserInput"/> writes.</para>
    ///
    /// <para>Step A (atomic claim): if <c>Status != Idle</c> or
    /// <c>PendingUserMessages.IsEmpty</c>, drop the trigger silently (a stale
    /// fingerprint emission arrived after the next round had already started,
    /// or pending drained between emission and claim). Otherwise flip
    /// <c>Status → StartingExecution</c> and stamp
    /// <c>ExecutionStartedAt</c>.</para>
    ///
    /// <para>After the claim, <see cref="ThreadSubmissionServer.DispatchAfterClaim"/>
    /// drives Step B (drain → Messages + materialise user cells) and Step C
    /// (allocate response cell, transition to <c>Executing</c>, post to
    /// <c>_Exec</c>).</para>
    /// </summary>
    internal static IMessageDelivery HandleStartExecution(
        IMessageHub hub, IMessageDelivery<StartExecutionTrigger> delivery)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        var threadPath = delivery.Message.ThreadPath;

        // Sanity check: the trigger should target this hub.
        if (!string.Equals(hub.Address.Path, threadPath, StringComparison.Ordinal))
        {
            logger?.LogWarning(
                "[HandleStartExecution] trigger landed on wrong hub: hub={Hub}, threadPath={ThreadPath}",
                hub.Address.Path, threadPath);
            hub.Post(new ThreadMutationAck(false, "wrong hub"), o => o.ResponseFor(delivery));
            return delivery.Processed();
        }

        // Step A: atomic claim. Returning `node` unchanged (no-op) signals
        // "drop the trigger" — the post-update emission carries the pre-claim
        // node so the Subscribe below checks Status and bails out.
        hub.GetWorkspace().GetMeshNodeStream().Update(node =>
        {
            var t = node.Content as MeshThread ?? new MeshThread();
            if (t.Status != ThreadExecutionStatus.Idle)
            {
                logger?.LogDebug(
                    "[HandleStartExecution] claim skipped: status={Status} (already running) for {ThreadPath}",
                    t.Status, threadPath);
                return node;
            }
            if (t.PendingUserMessages.IsEmpty)
            {
                logger?.LogDebug(
                    "[HandleStartExecution] claim skipped: PendingUserMessages empty for {ThreadPath}",
                    threadPath);
                return node;
            }
            logger?.LogInformation(
                "[HandleStartExecution] CLAIMED: {ThreadPath} pending={Pending} → Status=StartingExecution",
                threadPath, t.PendingUserMessages.Count);
            return node with
            {
                Content = t with
                {
                    Status = ThreadExecutionStatus.StartingExecution,
                    ExecutionStartedAt = DateTime.UtcNow
                }
            };
        }).Subscribe(
            postClaim =>
            {
                // Did we actually claim it? If Status didn't flip, drop.
                if ((postClaim.Content as MeshThread)?.Status != ThreadExecutionStatus.StartingExecution)
                    return;
                // Hand off to Step B + C.
                ThreadSubmissionServer.DispatchAfterClaim(hub, postClaim, logger);
            },
            ex => logger?.LogWarning(ex,
                "[HandleStartExecution] claim Update failed for {ThreadPath}", threadPath));

        hub.Post(new ThreadMutationAck(true), o => o.ResponseFor(delivery));
        return delivery.Processed();
    }

    /// <summary>
    /// Handler for <see cref="ResubmitTrigger"/>: re-dispatches to OWN context.
    /// Registered on the per-thread hub by <see cref="ThreadExecution.AddThreadExecution"/>.
    /// </summary>
    internal static IMessageDelivery HandleResubmitTrigger(
        IMessageHub hub, IMessageDelivery<ResubmitTrigger> delivery)
    {
        var t = delivery.Message;
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.ThreadSubmission");
        logger?.LogInformation("[HandleResubmitTrigger] on {Hub} threadPath={ThreadPath} msg={MsgId}",
            hub.Address, t.ThreadPath, t.UserMessageId);
        ApplyResubmitOwn(hub, t.ThreadPath, t.UserMessageId, t.NewUserText, t.AgentName, t.ModelName);
        hub.Post(new ThreadMutationAck(true), o => o.ResponseFor(delivery));
        return delivery.Processed();
    }

    internal static IMessageDelivery HandleDeleteFromMessageTrigger(
        IMessageHub hub, IMessageDelivery<DeleteFromMessageTrigger> delivery)
    {
        var t = delivery.Message;
        ApplyDeleteFromMessageOwn(hub, t.ThreadPath, t.MessageId);
        hub.Post(new ThreadMutationAck(true), o => o.ResponseFor(delivery));
        return delivery.Processed();
    }

    internal static IMessageDelivery HandleRecordSubmissionFailureTrigger(
        IMessageHub hub, IMessageDelivery<RecordSubmissionFailureTrigger> delivery)
    {
        var t = delivery.Message;
        ApplyRecordSubmissionFailureOwn(hub, t.ThreadPath, t.UserMessageId, t.UserText, t.ErrorMessage);
        hub.Post(new ThreadMutationAck(true), o => o.ResponseFor(delivery));
        return delivery.Processed();
    }

    /// <summary>
    /// Truncates the thread's <see cref="MeshThread.Messages"/> at
    /// <paramref name="atMessageId"/> (exclusive — drops messageId and
    /// everything after). Fans out to the OWN-update fast path when invoked
    /// from the per-thread hub itself, and to a posted
    /// <see cref="DeleteFromMessageTrigger"/> (handled inline on the thread
    /// hub in OWN context) when invoked from any other hub. The trigger hop
    /// exists because UpdateRemote currently re-runs the lambda against a
    /// stale baseline. See <c>RequestViaStreamUpdate.md</c>.
    /// </summary>
    public static void ApplyDeleteFromMessage(
        IMessageHub hub,
        string threadPath,
        string atMessageId)
    {
        if (!string.Equals(hub.Address.Path, threadPath, StringComparison.Ordinal))
        {
            hub.Post(new DeleteFromMessageTrigger(threadPath, atMessageId),
                o => o.WithTarget(new Address(threadPath)));
            return;
        }
        ApplyDeleteFromMessageOwn(hub, threadPath, atMessageId);
    }

    private static void ApplyDeleteFromMessageOwn(IMessageHub hub, string threadPath, string atMessageId)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.ThreadSubmission");
        hub.GetWorkspace().GetMeshNodeStream().Update(node =>
        {
            var t = node.Content as MeshThread ?? new MeshThread();
            var idx = t.Messages.IndexOf(atMessageId);
            if (idx < 0) return node;
            return node with
            {
                Content = t with { Messages = t.Messages.Take(idx).ToImmutableList() }
            };
        }).Subscribe(
            _ => { },
            ex => logger?.LogWarning(ex,
                "ApplyDeleteFromMessage: UpdateMeshNode failed for thread {ThreadPath} message {MessageId}",
                threadPath, atMessageId));
    }

    /// <summary>
    /// Truncates the thread after <paramref name="userMessageId"/>, drops it
    /// from IngestedMessageIds so the watcher re-dispatches a new round, and
    /// optionally updates the user cell text. Fans out to the OWN-update fast
    /// path when invoked from the per-thread hub itself, and to a posted
    /// <see cref="ResubmitTrigger"/> (handled inline on the thread hub in OWN
    /// context) when invoked from any other hub. See
    /// <c>RequestViaStreamUpdate.md</c>.
    /// </summary>
    public static void ApplyResubmit(
        IMessageHub hub,
        string threadPath,
        string userMessageId,
        string? newUserText,
        string? agentName,
        string? modelName)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.ThreadSubmission");
        if (!string.Equals(hub.Address.Path, threadPath, StringComparison.Ordinal))
        {
            logger?.LogInformation(
                "[ApplyResubmit] cross-hub: posting ResubmitTrigger from {Hub} to {ThreadPath} (msg={MsgId})",
                hub.Address, threadPath, userMessageId);
            hub.Post(new ResubmitTrigger(threadPath, userMessageId, newUserText, agentName, modelName),
                o => o.WithTarget(new Address(threadPath)));
            return;
        }
        logger?.LogInformation("[ApplyResubmit] own-hub: running inline on {ThreadPath} (msg={MsgId})", threadPath, userMessageId);
        ApplyResubmitOwn(hub, threadPath, userMessageId, newUserText, agentName, modelName);
    }

    internal static void ApplyResubmitOwn(
        IMessageHub hub,
        string threadPath,
        string userMessageId,
        string? newUserText,
        string? agentName,
        string? modelName)
    {
        // Optionally update the user cell text. Target the thread address
        // (not the caller's own address — the cell lives under the thread).
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
            hub.Post(new UpdateNodeRequest(updatedCell), o => o.WithTarget(new Address(threadPath)));
        }

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.ThreadSubmission");
        hub.GetWorkspace().GetMeshNodeStream().Update(node =>
        {
            var t = node.Content as MeshThread ?? new MeshThread();
            var idx = t.Messages.IndexOf(userMessageId);
            if (idx < 0) return node;

            // Truncate Messages strictly BEFORE the replayed id — the user
            // message moves OUT of Messages and BACK into PendingUserMessages
            // so the inbox re-ingests it on the next round. The paired response
            // cell (and anything later) drops.
            var keep = t.Messages.Take(idx).ToImmutableList();
            var trimmedUserIds = t.UserMessageIds
                .Where(uid => keep.Contains(uid) || uid == userMessageId)
                .ToImmutableList();
            if (!trimmedUserIds.Contains(userMessageId))
                trimmedUserIds = trimmedUserIds.Add(userMessageId);
            var ingested = t.IngestedMessageIds.Remove(userMessageId);
            // Re-queue: put the replayed message back into PendingUserMessages
            // so the watcher fires StartExecutionTrigger for the new round.
            var replayMessage = new ThreadMessage
            {
                Role = "user",
                Text = newUserText ?? "",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput,
                AgentName = agentName,
                ModelName = modelName
            };
            var pending = t.PendingUserMessages.SetItem(userMessageId, replayMessage);
            return node with
            {
                Content = t with
                {
                    Messages = keep,
                    UserMessageIds = trimmedUserIds,
                    IngestedMessageIds = ingested,
                    PendingUserMessages = pending,
                    Status = ThreadExecutionStatus.Idle,
                    ActiveMessageId = null,
                    ExecutionStartedAt = null,
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
    /// <summary>
    /// Notifier-only watcher: posts <see cref="StartExecutionTrigger"/> to the
    /// thread hub whenever it observes <c>Status == Idle</c> with a non-empty
    /// pending queue. ALL round-state mutation happens in
    /// <see cref="ThreadSubmission.HandleStartExecution"/> (atomic claim) and
    /// <see cref="DispatchAfterClaim"/> (drain + cell creation). The watcher
    /// never touches Messages, Status, or PendingUserMessages directly.
    ///
    /// <para>Single-flight is guaranteed by the atomic claim in the handler:
    /// concurrent triggers (from rapid emissions or post-pile-up) all enter
    /// the action block serially; the first claim wins and flips Status, the
    /// rest see <c>Status != Idle</c> and drop.</para>
    /// </summary>
    public static IDisposable InstallServerWatcher(IMessageHub threadHub)
    {
        var logger = threadHub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        return threadHub.GetWorkspace().GetMeshNodeStream()
            .Select(node => Fingerprint(node))
            .DistinctUntilChanged()
            .Where(fp => fp.NeedsDispatch)
            .Subscribe(
                _ =>
                {
                    var threadPath = threadHub.Address.Path;
                    logger?.LogDebug(
                        "[SubmissionWatcher] posting StartExecutionTrigger for {ThreadPath}",
                        threadPath);
                    threadHub.Post(
                        new StartExecutionTrigger(threadPath),
                        o => o.WithTarget(threadHub.Address));
                },
                ex => logger?.LogWarning(ex,
                    "[SubmissionWatcher] stream errored for {ThreadPath}",
                    threadHub.Address.Path));
    }

    private record struct WatcherFingerprint(
        ThreadExecutionStatus Status,
        int PendingCount,
        bool NeedsDispatch);

    /// <summary>
    /// Compresses the dispatchable state into a value tuple that drives
    /// <c>DistinctUntilChanged</c>. NeedsDispatch is true when the thread is
    /// <see cref="ThreadExecutionStatus.Idle"/> and at least one entry is
    /// queued in <c>PendingUserMessages</c>.
    /// </summary>
    private static WatcherFingerprint Fingerprint(MeshNode? node)
    {
        if (node?.Content is not MeshThread t)
            return new WatcherFingerprint(ThreadExecutionStatus.Idle, 0, false);
        var needs = t.Status == ThreadExecutionStatus.Idle && t.PendingUserMessages.Count > 0;
        return new WatcherFingerprint(t.Status, t.PendingUserMessages.Count, needs);
    }

    /// <summary>
    /// Step B + Step C of the round, called from
    /// <see cref="ThreadSubmission.HandleStartExecution"/> after the atomic
    /// claim succeeded (Status flipped to <see cref="ThreadExecutionStatus.StartingExecution"/>).
    /// Drains all pending entries into <see cref="MeshThread.Messages"/>,
    /// materialises user satellite cells, allocates a single response cell,
    /// transitions <see cref="ThreadExecutionStatus.StartingExecution"/> →
    /// <see cref="ThreadExecutionStatus.Executing"/>, and posts to <c>_Exec</c>
    /// for streaming.
    /// </summary>
    internal static void DispatchAfterClaim(
        IMessageHub hub, MeshNode threadNode, ILogger<AgentChatClient>? logger)
    {
        var thread = threadNode.Content as MeshThread;
        if (thread is null)
        {
            logger?.LogWarning(
                "[DispatchAfterClaim] thread node has no MeshThread content for {Path}",
                hub.Address.Path);
            return;
        }
        var dispatch = ThreadSubmission.PlanNextRound(thread);
        if (dispatch is null)
        {
            logger?.LogDebug(
                "[DispatchAfterClaim] nothing to dispatch (post-claim race?) for {Path} — rolling status back to Idle",
                hub.Address.Path);
            // Roll the claim back so the next watcher tick can re-trigger.
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
        DispatchRound(hub, threadNode, dispatch, logger);
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
                            // We expect Status==StartingExecution (post-claim from HandleStartExecution).
                            // Anything else is an out-of-band state change — drop the commit.
                            if (t.Status != ThreadExecutionStatus.StartingExecution) return node;

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
                                    Status = ThreadExecutionStatus.Executing,
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
