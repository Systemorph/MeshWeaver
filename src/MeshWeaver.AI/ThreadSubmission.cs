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
    /// Submits a user message into an existing thread. Fire-and-forget; the caller
    /// observes the new user cell appear through the thread's remote MeshNode stream.
    /// </summary>
    public static void Submit(SubmitContext ctx)
        => ThreadSubmissionClient.Submit(ctx);

    /// <summary>
    /// Creates a new thread node and submits the first user message. <see cref="SubmitContext.OnThreadCreated"/>
    /// fires when the thread node is confirmed so the caller can navigate immediately.
    /// </summary>
    public static void CreateThreadAndSubmit(SubmitContext ctx)
        => ThreadSubmissionClient.CreateThreadAndSubmit(ctx);

    /// <summary>
    /// Resubmits an existing user message: truncates <c>Messages</c> and <c>IngestedMessageIds</c>
    /// after the replayed id, optionally updating the user cell text. The server watcher
    /// creates a new output cell.
    /// </summary>
    public static void Resubmit(ResubmitContext ctx)
        => ThreadSubmissionClient.Resubmit(ctx);

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
    /// Thread-hub handler: registers a new user message id on the thread, stores Pending*,
    /// and lets the watcher dispatch. Runs on the thread hub's scheduler — only one
    /// AppendUserMessageRequest is processed at a time, so the state update is atomic
    /// and patch-safe.
    /// </summary>
    public static IMessageDelivery HandleAppendUserMessage(
        IMessageHub hub,
        IMessageDelivery<AppendUserMessageRequest> delivery)
    {
        var req = delivery.Message;
        hub.GetWorkspace().UpdateMeshNode(node =>
        {
            var t = node.Content as MeshThread ?? new MeshThread();
            var msgs = t.Messages.Contains(req.UserMessageId) ? t.Messages : t.Messages.Add(req.UserMessageId);
            var userIds = t.UserMessageIds.Contains(req.UserMessageId) ? t.UserMessageIds : t.UserMessageIds.Add(req.UserMessageId);
            // Accumulate queued text into PendingUserMessage. DispatchRound reads and clears it.
            var pending = string.IsNullOrEmpty(t.PendingUserMessage)
                ? req.UserText
                : $"{t.PendingUserMessage}\n\n---\n\n{req.UserText}";
            return node with
            {
                Content = t with
                {
                    Messages = msgs,
                    UserMessageIds = userIds,
                    PendingUserMessage = pending,
                    PendingAgentName = req.AgentName ?? t.PendingAgentName,
                    PendingModelName = req.ModelName ?? t.PendingModelName,
                    PendingContextPath = req.ContextPath ?? t.PendingContextPath,
                    PendingAttachments = req.Attachments?.ToImmutableList() ?? t.PendingAttachments
                }
            };
        });
        hub.Post(new AppendUserMessageResponse { Success = true }, o => o.ResponseFor(delivery));
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
        hub.Post(new CreateNodeRequest(errorCell), o => o.WithTarget(hub.Address));

        // Update thread state: link user message (if missing) + error response + mark ingested.
        hub.GetWorkspace().UpdateMeshNode(node =>
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
/// Client-side submission logic. All methods are void / fire-and-forget.
/// The client only posts CreateNodeRequest — the server watcher does all Thread-state bookkeeping
/// (append to Messages/UserMessageIds, set Pending*, dispatch). This avoids remote-stream write
/// races that produce out-of-bounds JSON patches.
/// </summary>
internal static class ThreadSubmissionClient
{
    private static string NewId() => Guid.NewGuid().ToString("N")[..8];

    public static void Submit(SubmitContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.ThreadPath))
        {
            ctx.OnError?.Invoke("Submit requires ThreadPath. Use CreateThreadAndSubmit for new threads.");
            return;
        }

        var userMsgId = NewId();
        var threadAddr = new Address(ctx.ThreadPath);
        var userCell = BuildUserCell(userMsgId, ctx.ThreadPath, ctx);

        void ReportFailure(string reason)
        {
            PostFailureRecord(ctx.Hub, ctx.ThreadPath!, userMsgId, ctx.UserText, reason);
            ctx.OnError?.Invoke(reason);
        }

        // 1) Create the user cell.
        var createDelivery = ctx.Hub.Post(
            new CreateNodeRequest(userCell),
            o => o.WithTarget(threadAddr));

        if (createDelivery != null)
        {
            ctx.Hub.RegisterCallback((IMessageDelivery)createDelivery, response =>
            {
                if (response is IMessageDelivery<CreateNodeResponse> { Message.Success: false } fail)
                    ReportFailure($"User cell creation failed: {fail.Message.Error ?? "unknown"}");
                return response;
            });
        }

        // 2) Tell the thread hub to register the id and queue it.
        var appendDelivery = ctx.Hub.Post(
            new AppendUserMessageRequest
            {
                ThreadPath = ctx.ThreadPath,
                UserMessageId = userMsgId,
                UserText = ctx.UserText,
                AgentName = ctx.AgentName,
                ModelName = ctx.ModelName,
                ContextPath = ctx.ContextPath,
                Attachments = ctx.Attachments
            },
            o => o.WithTarget(threadAddr));

        if (appendDelivery != null)
        {
            ctx.Hub.RegisterCallback((IMessageDelivery)appendDelivery, response =>
            {
                if (response is IMessageDelivery<AppendUserMessageResponse> { Message.Success: false } fail)
                    ReportFailure($"Append failed: {fail.Message.Error ?? "unknown"}");
                return response;
            });
        }
    }

    public static void CreateThreadAndSubmit(SubmitContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.Namespace))
        {
            ctx.OnError?.Invoke("CreateThreadAndSubmit requires Namespace.");
            return;
        }

        var userMsgId = NewId();

        // Build an empty thread node. The server watcher will populate Messages/UserMessageIds
        // once the user cell is created.
        var threadNode = ThreadNodeType.BuildThreadNode(ctx.Namespace, ctx.UserText, ctx.CreatedBy);
        var threadPath = threadNode.Path!;
        var userCell = BuildUserCell(userMsgId, threadPath, ctx);

        var delivery = ctx.Hub.Post(
            new CreateNodeRequest(threadNode),
            o => o.WithTarget(new Address(ctx.Namespace)));

        if (delivery == null)
        {
            ctx.OnError?.Invoke("Hub.Post returned null");
            return;
        }

        ctx.Hub.RegisterCallback((IMessageDelivery)delivery, response =>
        {
            if (response is not IMessageDelivery<CreateNodeResponse> { Message.Success: true } cnr)
            {
                var err = (response as IMessageDelivery<CreateNodeResponse>)?.Message.Error ?? "unknown";
                ctx.OnError?.Invoke($"Thread creation failed: {err}");
                return response;
            }

            var createdNode = cnr.Message.Node ?? threadNode;
            var createdPath = createdNode.Path ?? threadPath;
            ctx.OnThreadCreated?.Invoke(createdNode);

            var threadAddr = new Address(createdPath);

            // Create the user cell on the new thread.
            var cellDelivery = ctx.Hub.Post(
                new CreateNodeRequest(userCell),
                o => o.WithTarget(threadAddr));

            if (cellDelivery is not null)
            {
                ctx.Hub.RegisterCallback((IMessageDelivery)cellDelivery, cellResp =>
                {
                    if (cellResp is IMessageDelivery<CreateNodeResponse> { Message.Success: false } cellFail)
                        ctx.OnError?.Invoke($"User cell creation failed: {cellFail.Message.Error ?? "unknown"}");
                    return cellResp;
                });
            }

            // Tell the thread hub to register the id + queue it.
            var appendDelivery = ctx.Hub.Post(
                new AppendUserMessageRequest
                {
                    ThreadPath = createdPath,
                    UserMessageId = userMsgId,
                    UserText = ctx.UserText,
                    AgentName = ctx.AgentName,
                    ModelName = ctx.ModelName,
                    ContextPath = ctx.ContextPath,
                    Attachments = ctx.Attachments
                },
                o => o.WithTarget(threadAddr));

            if (appendDelivery is not null)
            {
                ctx.Hub.RegisterCallback((IMessageDelivery)appendDelivery, appendResp =>
                {
                    if (appendResp is IMessageDelivery<AppendUserMessageResponse> { Message.Success: false } fail)
                        ctx.OnError?.Invoke($"Append failed: {fail.Message.Error ?? "unknown"}");
                    return appendResp;
                });
            }

            return response;
        });
    }

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

        ctx.Hub.RegisterCallback((IMessageDelivery)delivery, response =>
        {
            if (response is IMessageDelivery<AppendUserMessageResponse> { Message.Success: false } fail)
                ctx.OnError?.Invoke($"Resubmit failed: {fail.Message.Error ?? "unknown"}");
            return response;
        });
    }

    /// <summary>
    /// Fire-and-forget post of a <see cref="RecordSubmissionFailureRequest"/> so the thread
    /// shows the failure as an error response cell. If this post also fails, we've exhausted
    /// recovery — swallow silently (the OnError callback is still invoked separately).
    /// </summary>
    private static void PostFailureRecord(
        IMessageHub hub, string threadPath, string userMsgId, string userText, string error)
    {
        try
        {
            hub.Post(
                new RecordSubmissionFailureRequest
                {
                    ThreadPath = threadPath,
                    UserMessageId = userMsgId,
                    UserText = userText,
                    ErrorMessage = error
                },
                o => o.WithTarget(new Address(threadPath)));
        }
        catch { /* swallow — caller's OnError will still fire */ }
    }

    private static MeshNode BuildUserCell(string userMsgId, string threadPath, SubmitContext ctx)
        => new(userMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = ctx.ContextPath ?? threadPath,
            Content = new ThreadMessage
            {
                Role = "user",
                AuthorName = ctx.AuthorName,
                Text = ctx.UserText,
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput,
                CreatedBy = ctx.CreatedBy,
                AgentName = ctx.AgentName,
                ModelName = ctx.ModelName,
                ContextPath = ctx.ContextPath,
                Attachments = ctx.Attachments
            }
        };
}

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
        // Combined with the thread's IsExecuting flag, prevents double-dispatch
        // between "start dispatching" and "IsExecuting=true visible on stream".
        var dispatching = 0;

        var sub = workspace.GetStream<MeshNode>()
            ?.Subscribe(nodes =>
            {
                if (nodes == null) return;
                if (Interlocked.CompareExchange(ref dispatching, 1, 0) != 0)
                    return;

                try
                {
                    var threadNode = nodes.FirstOrDefault(n => n.Path == threadPath);
                    if (threadNode?.Content is not MeshThread thread) return;

                    // Queue-don't-cancel: if the thread is executing, do nothing. The queued
                    // user messages stay in UserMessageIds; as soon as IsExecuting flips to
                    // false (current round completed naturally), we dispatch the next round.
                    // This matches Claude Code / Anthropic's recommended pattern — the Messages
                    // API doesn't support mid-stream injection and cancelling during a tool_use
                    // produces orphaned blocks that need synthetic tool_result recovery.
                    if (thread.IsExecuting) return;

                    var dispatch = ThreadSubmission.PlanNextRound(thread);
                    if (dispatch is null) return;

                    DispatchRound(threadHub, threadNode, dispatch, logger);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "[ThreadSubmission] Server watcher iteration failed for {ThreadPath}", threadPath);
                }
                finally
                {
                    Interlocked.Exchange(ref dispatching, 0);
                }
            });

        return sub ?? System.Reactive.Disposables.Disposable.Empty;
    }

    /// <summary>
    /// Creates the output cell, writes the committed round to the thread node, and
    /// fires off agent execution on the _Exec hosted hub. Non-blocking — all
    /// Hub.Post + RegisterCallback; the workspace write is a synchronous fire-and-forget.
    /// </summary>
    private static void DispatchRound(
        IMessageHub hub,
        MeshNode threadNode,
        RoundDispatch dispatch,
        ILogger<AgentChatClient>? logger)
    {
        var threadPath = hub.Address.Path;
        var responseMsgId = dispatch.ResponseMessageId;
        var responsePath = $"{threadPath}/{responseMsgId}";
        var thread = threadNode.Content as MeshThread ?? new MeshThread();
        var mainEntity = threadNode.MainNode ?? dispatch.ContextPath ?? threadPath;

        // PendingUserMessage contains the concatenated text of all user messages queued by
        // AppendUserMessageRequest handlers since the last dispatch.
        var combinedUserText = thread.PendingUserMessage ?? "";

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var userCtx = accessService?.Context ?? accessService?.CircuitContext;
        if (userCtx is null && !string.IsNullOrEmpty(thread.CreatedBy))
        {
            userCtx = new AccessContext { ObjectId = thread.CreatedBy, Name = thread.CreatedBy };
        }

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
            o => userCtx != null ? o.WithAccessContext(userCtx).WithTarget(hub.Address) : o.WithTarget(hub.Address));

        if (createDelivery == null)
        {
            logger?.LogWarning("[ThreadSubmission] Post of CreateNodeRequest returned null for response cell {ResponseMsgId} on {ThreadPath}",
                responseMsgId, threadPath);
            return;
        }

        hub.RegisterCallback((IMessageDelivery)createDelivery, response =>
        {
            if (response is not IMessageDelivery<CreateNodeResponse> { Message.Success: true })
            {
                var err = (response as IMessageDelivery<CreateNodeResponse>)?.Message.Error ?? "unknown";
                logger?.LogWarning("[ThreadSubmission] Response cell creation failed for {ResponseMsgId} on {ThreadPath}: {Error}",
                    responseMsgId, threadPath, err);
                return response;
            }

            // Step 2: commit the round to the thread state (one atomic UpdateMeshNode).
            hub.GetWorkspace().UpdateMeshNode(node =>
            {
                var t = node.Content as MeshThread ?? new MeshThread();
                var msgs = t.Messages.Contains(responseMsgId) ? t.Messages : t.Messages.Add(responseMsgId);
                var ingested = t.IngestedMessageIds;
                foreach (var uid in dispatch.UserMessageIds)
                {
                    if (!ingested.Contains(uid))
                        ingested = ingested.Add(uid);
                }
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
                        // Clear PendingUserMessage — the round's text is already captured in combinedUserText.
                        // Next AppendUserMessageRequest starts accumulating fresh for the next round.
                        PendingUserMessage = null,
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

            return response;
        });
    }
}
