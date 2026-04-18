using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
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
    /// <summary>
    /// Registers thread execution handlers on a hub configuration.
    /// Includes a startup recovery check for stale executing cells from crashed sessions.
    /// </summary>
    /// <summary>
    /// Stores completion callbacks keyed by thread path.
    /// Used to route ExecutionCompleted responses from the _Exec hub
    /// back to the original client delivery on the thread hub.
    /// Safe: thread hub + _Exec hub always run on the same grain/process.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Action<SubmitMessageResponse>> CompletionCallbacks = new();
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> ExecutionCancellations = new();
    private static readonly ConcurrentDictionary<string, AgentChatClient> AgentCache = new();

    public static MessageHubConfiguration AddThreadExecution(this MessageHubConfiguration configuration)
        => configuration
            .WithHandler<SubmitMessageRequest>(HandleSubmitMessage)
            .WithHandler<AppendUserMessageRequest>(ThreadSubmission.HandleAppendUserMessage)
            .WithHandler<ResubmitUserMessageRequest>(ThreadSubmission.HandleResubmitUserMessage)
            .WithHandler<RecordSubmissionFailureRequest>(ThreadSubmission.HandleRecordSubmissionFailure)
            .WithHandler<CancelThreadStreamRequest>(HandleCancelStream)
            .WithInitialization(SetThreadHubIdentity)
            .WithInitialization(RecoverStaleExecutingThread)
            .WithInitialization(WatchForExecution)
            .WithInitialization(InstallSubmissionWatcher);

    /// <summary>
    /// Installs the continuous server-side watcher that ingests queued user messages
    /// into new rounds and dispatches agent execution. See <see cref="ThreadSubmission"/>.
    /// </summary>
    private static Task InstallSubmissionWatcher(IMessageHub hub, CancellationToken ct)
    {
        var sub = ThreadSubmission.InstallServerWatcher(hub);
        // Dispose with the hub lifetime.
        hub.RegisterForDisposal(sub);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels the active execution on <paramref name="threadPath"/> — used by the explicit
    /// user "Stop" button. Do NOT call this automatically when queued user messages arrive
    /// during execution: the Anthropic Messages API does not support mid-stream injection,
    /// and cancelling during a tool_use produces orphaned tool_use blocks that require a
    /// synthetic error tool_result to recover. The correct pattern for queued input is
    /// "wait for the round to complete, then dispatch a fresh round with all queued
    /// messages in history". See ThreadSubmissionServer.InstallServerWatcher.
    /// Idempotent — repeated calls during the same round are no-ops.
    /// </summary>
    internal static void RequestSafeCancellation(string threadPath)
    {
        if (ExecutionCancellations.TryGetValue(threadPath, out var cts) && !cts.IsCancellationRequested)
        {
            try { cts.Cancel(); } catch { /* already disposed */ }
        }
    }

    /// <summary>
    /// Sets the thread hub's access context to the thread creator's identity.
    /// Without this, the hub's default identity is its own address path,
    /// causing "Access denied" when reading child message nodes.
    /// </summary>
    private static Task SetThreadHubIdentity(IMessageHub hub, CancellationToken ct)
    {
        hub.GetWorkspace().GetStream(new MeshNodeReference())?.Take(1).Subscribe(node =>
        {
            if (node.Value?.Content is MeshThread { CreatedBy: { Length: > 0 } createdBy })
            {
                var accessService = hub.ServiceProvider.GetService<AccessService>();
                accessService?.SetContext(new AccessContext { ObjectId = createdBy, Name = createdBy });
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// On hub startup, check if this Thread was left in IsExecuting=true state (crashed/restarted).
    /// If stale: mark the active response message as "*Cancelled*", clear execution state,
    /// and mark all ActiveProgress entries as completed. Fully non-blocking — no await.
    /// Each child thread's own hub recovery handles its own cancellation recursively.
    /// </summary>
    private static Task RecoverStaleExecutingThread(IMessageHub hub, CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        var workspace = hub.GetWorkspace();
        var threadPath = hub.Address.Path;

        // Read the thread node from the workspace stream (already loaded on hub init)
        workspace.GetStream<MeshNode>()?.Take(1).Subscribe(nodes =>
        {
            var threadNode = nodes?.FirstOrDefault(n => n.Path == threadPath);
            if (threadNode?.Content is not Thread { IsExecuting: true } thread)
                return;

            // Don't recover fresh executions — WatchForExecution handles them.
            // Only recover truly stale ones (started > 2 minutes ago or no timestamp).
            if (thread.ExecutionStartedAt is { } startedAt &&
                (DateTime.UtcNow - startedAt).TotalMinutes < 2)
            {
                logger?.LogInformation("[ThreadExec] Recovery: skipping fresh execution on {ThreadPath} (started {StartedAt})", threadPath, startedAt);
                return;
            }

            logger?.LogInformation("[ThreadExec] Recovery: stale execution on {ThreadPath}, activeMsg={ActiveMsg}",
                threadPath, thread.ActiveMessageId);

            // Cancel pending tool calls on the active response message.
            // For delegation tool calls, check if the sub-thread actually completed.
            if (!string.IsNullOrEmpty(thread.ActiveMessageId))
            {
                var responsePath = $"{threadPath}/{thread.ActiveMessageId}";

                // Mark all pending tool calls as cancelled — no query needed.
                // Sub-thread recovery happens independently on their own hub init.
                var updatedToolCalls = thread.StreamingToolCalls?
                    .Select(tc => tc.Result != null
                        ? tc
                        : tc with { Result = "Cancelled (server restarted)", IsSuccess = false })
                    .ToImmutableList();

                hub.Post(new UpdateThreadMessageContent
                {
                    Text = "*Cancelled (server restarted)*",
                    ToolCalls = updatedToolCalls
                }, o => o.WithTarget(new Address(responsePath)));
            }

            // Clear thread execution state
            workspace.UpdateMeshNode(node =>
            {
                var t = node.Content as Thread ?? new Thread();
                var cancelledAt = DateTime.UtcNow;
                return node with
                {
                    LastModified = cancelledAt,
                    Content = t with
                    {
                        IsExecuting = false,
                        ExecutionStatus = null,
                        ActiveMessageId = null,
                        TokensUsed = 0,
                        ExecutionStartedAt = null,
                        StreamingText = null,
                        StreamingToolCalls = null
                    }
                };
            });

            logger?.LogInformation("[ThreadExec] Recovery: cleared stale execution on {ThreadPath}", threadPath);
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// On hub startup, check if this Thread has a PendingUserMessage.
    /// If so, create message cells and start execution automatically.
    /// This enables thread creation + execution in a single CreateNodeRequest.
    /// </summary>
    /// <summary>
    /// Watches the workspace stream for IsExecuting=true with ActiveMessageId.
    /// When detected, starts execution on the _Exec hosted hub.
    /// This is the ONLY trigger for execution — state-driven, not command-driven.
    /// GUI sets IsExecuting=true via SubmitMessageRequest → execution starts automatically.
    /// </summary>
    /// <summary>
    /// Watches for auto-execute threads (created with BuildThreadWithMessages).
    /// These threads have PendingUserMessage set at creation time (not via HandleSubmitMessage).
    /// Creates message cells and starts execution on hub startup.
    /// HandleSubmitMessage handles all client-initiated execution directly.
    /// </summary>
    private static Task WatchForExecution(IMessageHub hub, CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        var workspace = hub.GetWorkspace();
        var threadPath = hub.Address.Path;

        // Only check on startup (Take(1)) — HandleSubmitMessage handles runtime execution.
        workspace.GetStream(new MeshNodeReference())?.Take(1).Subscribe(node =>
        {
            if (node.Value?.Content is not MeshThread { PendingUserMessage: not null } thread)
                return;

            // Only auto-execute threads created with BuildThreadWithMessages
            if (!thread.IsExecuting || thread.ActiveMessageId == null)
                return;

            var responseMsgId = thread.ActiveMessageId;
            var responsePath = $"{threadPath}/{responseMsgId}";
            var activeIdx = thread.Messages.IndexOf(responseMsgId);
            var userMsgId = activeIdx > 0 ? thread.Messages[activeIdx - 1] : null;
            // MainNode for child cells = the thread's own MainNode (content node).
            var mainEntity = node.Value?.MainNode ?? thread.PendingContextPath ?? threadPath;

            logger?.LogInformation("[ThreadExec] Auto-execute: {ThreadPath}, activeMsg={ActiveMsg}",
                threadPath, responseMsgId);

            var accessService = hub.ServiceProvider.GetService<AccessService>();
            if (!string.IsNullOrEmpty(thread.CreatedBy))
                accessService?.SetContext(new AccessContext { ObjectId = thread.CreatedBy, Name = thread.CreatedBy });

            var userCtx = !string.IsNullOrEmpty(thread.CreatedBy)
                ? new AccessContext { ObjectId = thread.CreatedBy, Name = thread.CreatedBy }
                : null;

            void StartExecution()
            {
                hub.Post(new UpdateThreadMessageContent { Text = "Allocating agent..." },
                    o => o.WithTarget(new Address(responsePath)));

                var executionHub = hub.GetHostedHub(
                    new Address($"{hub.Address}/_Exec"),
                    config => config.WithHandler<SubmitMessageRequest>(ExecuteMessageAsync),
                    HostedHubCreation.Always);

                executionHub!.Post(new SubmitMessageRequest
                {
                    ThreadPath = threadPath,
                    UserMessageText = thread.PendingUserMessage ?? "",
                    UserMessageId = userMsgId,
                    ResponseMessageId = responseMsgId,
                    ResponsePath = responsePath,
                    AgentName = thread.PendingAgentName,
                    ModelName = thread.PendingModelName,
                    ContextPath = thread.PendingContextPath ?? thread.CreatedBy,
                    Attachments = thread.PendingAttachments
                }, o => userCtx != null ? o.WithAccessContext(userCtx) : o);
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
                        Role = "user", Text = thread.PendingUserMessage, Timestamp = DateTime.UtcNow,
                        Type = ThreadMessageType.ExecutedInput, CreatedBy = thread.CreatedBy
                    }
                }).Subscribe(_ => { },
                    error => logger?.LogDebug("[ThreadExec] User cell creation error: {Error}", error.Message));
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles SubmitMessageRequest: updates thread state, responds immediately,
    /// then starts execution.
    /// - GUI flow (client provides UserMessageId + ResponseMessageId): cells already exist,
    ///   start execution directly.
    /// - Server flow (no IDs provided): set PendingUserMessage so WatchForExecution
    ///   creates cells and starts execution.
    /// </summary>
    internal static IMessageDelivery HandleSubmitMessage(
        IMessageHub hub,
        IMessageDelivery<SubmitMessageRequest> delivery)
    {
        var request = delivery.Message;
        var threadPath = request.ThreadPath;
        var logger = hub.ServiceProvider.GetService<ILogger<AgentChatClient>>();

        var clientProvidedCells = request.UserMessageId != null && request.ResponseMessageId != null;
        var userMsgId = request.UserMessageId ?? Guid.NewGuid().ToString("N")[..8];
        var responseMsgId = request.ResponseMessageId ?? Guid.NewGuid().ToString("N")[..8];
        var responsePath = $"{threadPath}/{responseMsgId}";

        // Update Thread state. Set PendingUserMessage so WatchForExecution
        // creates cells when they don't exist (server flow, delegation flow).
        hub.GetWorkspace().UpdateMeshNode(node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            var msgs = thread.Messages;
            if (!msgs.Contains(userMsgId)) msgs = msgs.Add(userMsgId);
            if (!msgs.Contains(responseMsgId)) msgs = msgs.Add(responseMsgId);
            return node with
            {
                Content = thread with
                {
                    Messages = msgs,
                    IsExecuting = true,
                    ActiveMessageId = responseMsgId,
                    ExecutionStatus = null,
                    TokensUsed = 0,
                    ExecutionStartedAt = DateTime.UtcNow,
                    PendingUserMessage = request.UserMessageText,
                    PendingAgentName = request.AgentName,
                    PendingModelName = request.ModelName,
                    PendingContextPath = request.ContextPath,
                    PendingAttachments = request.Attachments?.ToImmutableList()
                }
            };
        });

        logger?.LogInformation("[ThreadExec] HandleSubmitMessage: state updated for {ThreadPath}, activeMsg={ActiveMsg}, clientCells={ClientCells}",
            threadPath, responseMsgId, clientProvidedCells);

        var userCtx = delivery.AccessContext;
        // MainNode for child cells = the thread's own MainNode (content node, e.g. "PartnerRe/AIConsulting").
        // Fall back to request.ContextPath, then threadPath. Read from the workspace to get the
        // thread node's actual MainNode — this is authoritative, not the client's ContextPath.
        var threadNode = hub.GetWorkspace().GetStream(new MeshNodeReference())?.Current?.Value;
        var mainEntity = threadNode?.MainNode ?? request.ContextPath ?? threadPath;

        void RespondAndStartExecution()
        {
            hub.Post(new SubmitMessageResponse { Success = true, Messages = ImmutableList.Create(userMsgId, responseMsgId) },
                o => o.ResponseFor(delivery));

            hub.Post(new UpdateThreadMessageContent { Text = "Allocating agent..." },
                o => o.WithTarget(new Address(responsePath)));

            var executionHub = hub.GetHostedHub(
                new Address($"{hub.Address}/_Exec"),
                config => config.WithHandler<SubmitMessageRequest>(ExecuteMessageAsync),
                HostedHubCreation.Always);

            executionHub!.Post(new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = request.UserMessageText,
                UserMessageId = userMsgId,
                ResponseMessageId = responseMsgId,
                ResponsePath = responsePath,
                AgentName = request.AgentName,
                ModelName = request.ModelName,
                ContextPath = request.ContextPath,
                Attachments = request.Attachments
            }, o => userCtx != null ? o.WithAccessContext(userCtx) : o);
        }

        void RespondWithError(string error)
        {
            logger?.LogWarning("[ThreadExec] Cell creation failed for {ThreadPath}: {Error}", threadPath, error);
            // Clear execution state since we're not starting
            hub.GetWorkspace().UpdateMeshNode(node =>
            {
                var t = node.Content as MeshThread ?? new MeshThread();
                return node with { Content = t with { IsExecuting = false, ActiveMessageId = null, ExecutionStartedAt = null } };
            });
            hub.Post(new SubmitMessageResponse { Success = false, Error = error },
                o => o.ResponseFor(delivery));
        }

        if (clientProvidedCells)
        {
            // GUI flow — cells already exist, respond and start immediately.
            RespondAndStartExecution();
        }
        else
        {
            // Server flow — create cells first, then respond and start execution.
            // Response cell creation gates execution; user cell is fire-and-forget.
            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

            meshService.CreateNode(new MeshNode(userMsgId, threadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = mainEntity,
                Content = new ThreadMessage
                {
                    Role = "user", Text = request.UserMessageText, Timestamp = DateTime.UtcNow,
                    Type = ThreadMessageType.ExecutedInput, CreatedBy = delivery.AccessContext?.ObjectId
                }
            }).Subscribe(
                _ => logger?.LogDebug("[ThreadExec] User cell created: {Path}", $"{threadPath}/{userMsgId}"),
                ex => logger?.LogDebug("[ThreadExec] User cell creation error (may already exist): {Error}", ex.Message));

            meshService.CreateNode(new MeshNode(responseMsgId, threadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType, MainNode = mainEntity,
                Content = new ThreadMessage
                {
                    Role = "assistant", Text = "", Timestamp = DateTime.UtcNow,
                    Type = ThreadMessageType.AgentResponse,
                    AgentName = request.AgentName, ModelName = request.ModelName
                }
            }).Subscribe(
                _ => RespondAndStartExecution(),
                ex => RespondWithError($"Failed to create response cell: {ex.Message}"));
        }

        return delivery.Processed();
    }

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
    internal static IMessageDelivery ExecuteMessageAsync(
        IMessageHub hub,
        IMessageDelivery<SubmitMessageRequest> delivery)
    {
        var request = delivery.Message;
        var parentHub = hub.Configuration.ParentHub!;
        var threadPath = request.ThreadPath;
        var responsePath = request.ResponsePath!;
        var responseMsgId = responsePath.Split('/').Last();
        var logger = parentHub.ServiceProvider.GetRequiredService<ILogger<AgentChatClient>>();
        var workspace = parentHub.ServiceProvider.GetRequiredService<IWorkspace>();

        // Helper: push content to response message hub.
        // Posts UpdateThreadMessageContent which is handled ON the response grain —
        // calls workspace.UpdateMeshNode() locally → sync stream → clients.
        void PushToResponseMessage(string text, ImmutableList<ToolCallEntry> toolCalls,
            ImmutableList<NodeChangeEntry> updatedNodes,
            string? agentName, string? modelName)
        {
            logger.LogInformation("[ThreadExec] PUSH_TO_MSG: responsePath={ResponsePath}, textLen={TextLen}, toolCalls={ToolCalls}, updatedNodes={UpdatedNodes}",
                responsePath, text.Length, toolCalls.Count, updatedNodes.Count);
            parentHub.Post(new UpdateThreadMessageContent
            {
                Text = text,
                ToolCalls = toolCalls,
                UpdatedNodes = updatedNodes,
                AgentName = agentName,
                ModelName = modelName
            }, o => o.WithTarget(new Address(responsePath)));
        }

        // Helper: update Thread execution state via parentHub workspace.
        // parentHub.GetWorkspace().UpdateMeshNode() is a synchronous function — no message needed.
        var threadWorkspace = parentHub.GetWorkspace();
        void UpdateThreadExecution(Func<MeshThread, MeshThread> mutate)
        {
            threadWorkspace.UpdateMeshNode(node =>
            {
                var thread = node.Content as MeshThread ?? new MeshThread();
                return node with { Content = mutate(thread) };
            });
        }

        // Set user access context
        var accessService = parentHub.ServiceProvider.GetService<AccessService>();
        if (delivery.AccessContext != null)
            accessService?.SetContext(delivery.AccessContext);

        // Reuse cached agent (skips 3+ seconds of agent initialization on 2nd+ message)
        var chatClient = AgentCache.GetOrAdd(threadPath, _ =>
        {
            var c = new AgentChatClient(parentHub.ServiceProvider);
            c.SetThreadId(threadPath);
            return c;
        });

        // Subscribe to Initialize: when agents are ready, start the streaming loop
        var initSub = chatClient.Initialize(request.ContextPath, request.ModelName)
            .Take(1) // first emission = agents ready
            .Subscribe(client =>
            {
                logger.LogInformation("[ThreadExec] Agents ready for {ThreadPath}, starting execution", threadPath);

                // Set context from remote stream
                if (!string.IsNullOrEmpty(request.ContextPath))
                {
                    var contextStream = workspace.GetRemoteStream<MeshNode>(
                        new Address(request.ContextPath), new MeshNodeReference());
                    client.SetContext(new AgentContext
                    {
                        Address = new Address(request.ContextPath),
                        Context = request.ContextPath,
                        Node = contextStream.Current?.Value
                    });
                }

                if (!string.IsNullOrEmpty(request.AgentName))
                    client.SetSelectedAgent(request.AgentName);
                if (request.Attachments is { Count: > 0 })
                    client.SetAttachments(request.Attachments);

                var userAccessContext = delivery.AccessContext;
                client.SetExecutionContext(new ThreadExecutionContext
                {
                    ThreadPath = threadPath,
                    ResponseMessageId = responseMsgId,
                    ContextPath = request.ContextPath,
                    UserAccessContext = userAccessContext
                });

                // Load history via GetDataRequest to each message — fully reactive
                PushToResponseMessage("Loading conversation history...", ImmutableList<ToolCallEntry>.Empty,
                    ImmutableList<NodeChangeEntry>.Empty, request.AgentName, request.ModelName);

                // Load history: subscribe to Thread stream → get Messages → GetDataRequest each → CombineLatest
                threadWorkspace.GetStream(new MeshNodeReference())!
                    .Select(node => (node.Value?.Content as MeshThread)?.Messages ?? ImmutableList<string>.Empty)
                    .Where(msgs => msgs.Count > 0)
                    .Take(1)
                    .Subscribe(allMsgIds =>
                {
                    // History = all messages EXCEPT the last one (response cell).
                    // The current user cell IS included — its text comes from the cell itself.
                    var historyMsgIds = allMsgIds.Count > 1
                        ? allMsgIds.Take(allMsgIds.Count - 1).ToImmutableList()
                        : ImmutableList<string>.Empty;
                    logger.LogInformation("[ThreadExec] Loading {Count} history messages via GetDataRequest for {ThreadPath}",
                        historyMsgIds.Count, threadPath);

                    // Post GetDataRequest to each message, collect responses
                    var historySubjects = historyMsgIds.Select(msgId =>
                    {
                        var subject = new System.Reactive.Subjects.AsyncSubject<(string Id, ThreadMessage? Msg)>();
                        logger.LogDebug("[ThreadExec] HISTORY_REQ: posting GetDataRequest for {MsgPath}",
                            $"{threadPath}/{msgId}");
                        var del = parentHub.Post(new GetDataRequest(new MeshNodeReference()),
                            o => o.WithTarget(new Address($"{threadPath}/{msgId}")));
                        if (del != null)
                        {
                            logger.LogDebug("[ThreadExec] HISTORY_REQ: posted, delivery={Id} for {MsgId}", del.Id, msgId);
                            parentHub.RegisterCallback((IMessageDelivery)del, resp =>
                            {
                                ThreadMessage? tmsg = null;
                                if (resp is IMessageDelivery<GetDataResponse> gdr)
                                    tmsg = (gdr.Message.Data as MeshNode)?.Content as ThreadMessage;
                                logger.LogDebug("[ThreadExec] HISTORY_RESP: {MsgId} → role={Role}, textLen={Len}, respType={Type}",
                                    msgId, tmsg?.Role ?? "(null)", tmsg?.Text?.Length ?? -1, resp.Message?.GetType().Name);
                                subject.OnNext((msgId, tmsg));
                                subject.OnCompleted();
                                return resp;
                            });
                        }
                        else
                        {
                            logger.LogDebug("[ThreadExec] HISTORY_REQ: Post returned null for {MsgId}", msgId);
                            subject.OnNext((msgId, null));
                            subject.OnCompleted();
                        }
                        return subject.AsObservable();
                    }).ToList();

                    // When all responses arrive (or timeout), build history and start streaming
                    var historyObs = historySubjects.Count > 0
                        ? Observable.CombineLatest(historySubjects).Take(1)
                            .Select(results =>
                            {
                                var lookup = results.Where(r => r.Msg != null).ToDictionary(r => r.Id, r => r.Msg!);
                                return historyMsgIds
                                    .Where(id => lookup.ContainsKey(id))
                                    .Select(id =>
                                    {
                                        var msg = lookup[id];
                                        var role = msg.Role == "user" ? ChatRole.User : ChatRole.Assistant;
                                        var text = msg.Text ?? "";

                                        // For assistant messages: prepend tool call summaries so the
                                        // agent knows what it did (tools called, data read, results)
                                        if (role == ChatRole.Assistant && msg.ToolCalls is { Count: > 0 })
                                        {
                                            var toolSummary = string.Join("\n", msg.ToolCalls.Select(tc =>
                                                $"[Tool: {tc.Name}({tc.Arguments ?? ""})" +
                                                (tc.Result != null ? $" → {tc.Result[..Math.Min(500, tc.Result.Length)]}" : "") +
                                                "]"));
                                            text = $"{toolSummary}\n\n{text}";
                                        }

                                        return new ChatMessage(role, text);
                                    })
                                    .ToImmutableList();
                            })
                            .Timeout(TimeSpan.FromSeconds(10))
                            .Catch<ImmutableList<ChatMessage>, Exception>(ex =>
                            {
                                logger.LogDebug("[ThreadExec] HISTORY_TIMEOUT: {ThreadPath}, {Count} messages requested, error={Error}",
                                    threadPath, historyMsgIds.Count, ex.Message);
                                return Observable.Return(ImmutableList<ChatMessage>.Empty);
                            })
                        : Observable.Return(ImmutableList<ChatMessage>.Empty);

                    historyObs.Take(1).Subscribe(chatHistory =>
                    {
                    logger.LogInformation("[ThreadExec] Assembled {Count}/{Total} history messages for {ThreadPath}",
                        chatHistory.Count, historyMsgIds.Count, threadPath);

                    var toolCallLog = ImmutableList<ToolCallEntry>.Empty;
                var nodeChangeLog = ImmutableList<NodeChangeEntry>.Empty;
                // responseText is captured after InvokeAsync creates it (see below)
                StringBuilder? capturedResponseText = null;
                client.ForwardNodeChange = entry => { nodeChangeLog = nodeChangeLog.Add(entry); };
                string? currentStatus = null;
                client.UpdateDelegationStatus = status =>
                {
                    currentStatus = status;
                    logger.LogInformation("[ThreadExec] DELEGATION_STATUS: threadPath={ThreadPath}, status={Status}, delegationPaths=[{Paths}]",
                        threadPath, status, string.Join(",", chatClient.DelegationPaths.Select(kv => $"{kv.Key}={kv.Value}")));
                    // Push immediately when delegation path becomes available —
                    // the streaming loop is blocked during tool execution so the
                    // throttle block never runs. This ensures the parent message
                    // shows the delegation link while the sub-thread executes.
                    if (chatClient.DelegationPaths.TryGetValue(status, out var delPath))
                    {
                        // Stamp the path on the first unmatched delegation tool call
                        var stamped = false;
                        toolCallLog = toolCallLog.Select(e =>
                        {
                            if (!stamped && e.Name.StartsWith("delegate_to") && e.DelegationPath == null)
                            {
                                stamped = true;
                                return e with { DelegationPath = delPath };
                            }
                            return e;
                        }).ToImmutableList();
                        // Preserve any previously streamed text
                        PushToResponseMessage(capturedResponseText?.ToString() ?? "", toolCallLog, nodeChangeLog,
                            request.AgentName, request.ModelName);
                    }
                };
                client.ForwardToolCall = entry => { toolCallLog = toolCallLog.Add(entry); };

                var agentDisplayName = request.AgentName ?? "Agent";

                // Build full message list: history (from GetDataRequest) + current message
                // chatHistory already includes the current user message (loaded from the cell).
                // Only add it if history is empty (delegation sub-thread, text from PendingUserMessage).
                var allMessages = chatHistory.Count > 0
                    ? chatHistory
                    : chatHistory.Add(new ChatMessage(ChatRole.User, request.UserMessageText));
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
                var executionCts = new CancellationTokenSource();
                ExecutionCancellations[threadPath] = executionCts;
                // Cancel Task.Run when the hub disposes (grain deactivation).
                // Without this, OnDeactivateAsync waits up to 120s for the Task.Run
                // that's stuck on an AI API call with no cancellation signal.
                hub.RegisterForDisposal(_ => executionCts.Cancel());
                // Push progress: generating
                PushToResponseMessage("Generating response...", ImmutableList<ToolCallEntry>.Empty,
                    ImmutableList<NodeChangeEntry>.Empty, request.AgentName, request.ModelName);

                _ = Task.Run(async () =>
                {
                    var ct = executionCts.Token;
                    var responseText = new StringBuilder();
                    capturedResponseText = responseText;
                    int? inputTokens = null;
                    int? outputTokens = null;
                    int? totalTokens = null;
                    try
                    {
                    logger.LogInformation("[ThreadExec] STREAMING_LOOP_ENTRY: {Time:HH:mm:ss.fff} threadPath={ThreadPath} (on thread pool)", DateTime.UtcNow, threadPath);
                    // Keep the grain alive during the entire execution — including tool calls
                    // and delegations where the streaming loop is blocked.
                    using var heartbeatSubscription = parentHub.BeginAsyncOperation();
                    var lastUpdate = DateTimeOffset.MinValue;
                    var pendingCalls = ImmutableDictionary<string, FunctionCallContent>.Empty;
                    string? lastCallKey = null;

                    // Pass ALL messages through the official AgentChatClient path
                    await foreach (var update in client.GetStreamingResponseAsync(allMessages, ct))
            {
                // Capture function call / delegation activity for execution status
                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent functionCall)
                    {
                        logger.LogDebug("[ThreadExec] TOOL_START: {Time:HH:mm:ss.fff} {Name} callId={CallId} args={Args}",
                            DateTime.UtcNow, functionCall.Name, functionCall.CallId,
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

                        // Add pending tool call to local log — will be pushed on next throttled update
                        // Skip if we already have an entry for this callKey (re-emitted content)
                        if (!isDuplicate)
                        {
                            toolCallLog = toolCallLog.Add(new ToolCallEntry
                            {
                                Name = functionCall.Name,
                                DisplayName = formatted,
                                Arguments = argsDetail,
                                Timestamp = DateTime.UtcNow
                            });
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
                            var idx = toolCallLog.FindIndex(e => e.Name == originalCall.Name && e.Result == null);
                            var existingDelegationPath = idx >= 0 ? toolCallLog[idx].DelegationPath : null;
                            var finalEntry = new ToolCallEntry
                            {
                                Name = originalCall.Name,
                                DisplayName = ToolStatusFormatter.Format(originalCall),
                                Arguments = SerializeArgs(originalCall.Arguments),
                                Result = Truncate(resultText),
                                IsSuccess = isSuccess,
                                DelegationPath = delegationPath ?? existingDelegationPath,
                                Timestamp = DateTime.UtcNow
                            };
                            toolCallLog = idx >= 0 ? toolCallLog.SetItem(idx, finalEntry) : toolCallLog.Add(finalEntry);
                            logger.LogDebug("[ThreadExec] TOOL_DONE: {Time:HH:mm:ss.fff} {Name} callId={CallId} delegation={Delegation} resultLen={ResultLen}",
                                DateTime.UtcNow, originalCall.Name, originalCall.CallId, delegationPath,
                                finalEntry.Result?.Length ?? 0);
                        }
                        currentStatus = null; // Tool call completed
                    }
                }

                if (!string.IsNullOrEmpty(update.Text))
                    responseText.Append(update.Text);

                // Push streaming content at ~1/3sec — reduced frequency to avoid
                // overloading the grain scheduler (messages expire if queue backs up).
                if (DateTimeOffset.UtcNow - lastUpdate > TimeSpan.FromMilliseconds(3000))
                {
                    // Stamp delegation paths on any unmatched delegation tool calls
                    var pathValues = chatClient.DelegationPaths.Values.ToList();
                    var pathIdx = 0;
                    toolCallLog = toolCallLog.Select(e =>
                    {
                        if (e.Name.StartsWith("delegate_to") && e.DelegationPath == null && pathIdx < pathValues.Count)
                            return e with { DelegationPath = pathValues[pathIdx++] };
                        return e;
                    }).ToImmutableList();

                    PushToResponseMessage(responseText.ToString(), toolCallLog, nodeChangeLog,
                        request.AgentName, request.ModelName);
                    lastUpdate = DateTimeOffset.UtcNow;
                }
            }

                    // Final update — aggregate node changes (merges sub-thread changes with min/max versions),
                    // include token usage + completion timestamp so the cell can show duration / tokens.
                    var aggregatedChanges = AggregateNodeChanges(nodeChangeLog);
                    if (totalTokens is null && (inputTokens.HasValue || outputTokens.HasValue))
                        totalTokens = (inputTokens ?? 0) + (outputTokens ?? 0);
                    logger.LogInformation("[ThreadExec] EXECUTION_COMPLETE: {Time:HH:mm:ss.fff} threadPath={ThreadPath}, responseLength={Length}, toolCalls={ToolCalls}, tokens={In}/{Out}/{Total}",
                        DateTime.UtcNow, threadPath, responseText.Length, toolCallLog.Count,
                        inputTokens, outputTokens, totalTokens);
                    var finalText = responseText.ToString();
                    parentHub.Post(new UpdateThreadMessageContent
                    {
                        Text = finalText,
                        ToolCalls = toolCallLog,
                        UpdatedNodes = aggregatedChanges,
                        AgentName = request.AgentName,
                        ModelName = request.ModelName,
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens,
                        TotalTokens = totalTokens,
                        CompletedAt = DateTime.UtcNow
                    }, o => o.WithTarget(new Address(responsePath)));
                    // Clear streaming state
                    UpdateThreadExecution(t => t with
                    {
                        IsExecuting = false, ExecutionStatus = null, ActiveMessageId = null,
                        ExecutionStartedAt = null, StreamingText = null, StreamingToolCalls = null,
                        PendingUserMessage = null, PendingAgentName = null, PendingModelName = null,
                        PendingContextPath = null, PendingAttachments = null
                    });
                    // Notify parent via SubmitMessageResponse so delegation callback resolves.
                    // Must post on the _Exec hub (hub) — the SubmitMessageResponse handler
                    // is registered there and forwards to the thread hub via ResponseFor.
                    NotifyParentCompletion(parentHub, threadPath, finalText, true, aggregatedChanges);
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogInformation("[ThreadExec] CANCELLED: {Time:HH:mm:ss.fff} threadPath={ThreadPath}", DateTime.UtcNow, threadPath);
                        var cancelText = (responseText.ToString() + "\n\n*Cancelled*").Trim();
                        PushToResponseMessage(cancelText, toolCallLog, nodeChangeLog, request.AgentName, request.ModelName);
                        UpdateThreadExecution(t => t with
                        {
                            IsExecuting = false, ExecutionStatus = null, ActiveMessageId = null,
                            ExecutionStartedAt = null, StreamingText = null, StreamingToolCalls = null
                        });
                        NotifyParentCompletion(parentHub, threadPath, cancelText, false, nodeChangeLog);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "[ThreadExec] ERROR: {Time:HH:mm:ss.fff} threadPath={ThreadPath}", DateTime.UtcNow, threadPath);
                        var errorText = (responseText.ToString() + $"\n\n*Error: {ex.Message}*").Trim();
                        PushToResponseMessage(errorText, toolCallLog, nodeChangeLog, request.AgentName, request.ModelName);
                        UpdateThreadExecution(t => t with
                        {
                            IsExecuting = false, ExecutionStatus = null, ActiveMessageId = null,
                            ExecutionStartedAt = null, StreamingText = null, StreamingToolCalls = null
                        });
                        NotifyParentCompletion(parentHub, threadPath, errorText, false, nodeChangeLog);
                    }
                    finally
                    {
                        ExecutionCancellations.TryRemove(threadPath, out _);
                        executionCts.Dispose();
                    }
                });
                    }); // end of historyObs.Subscribe
                }); // end of threadStream.Subscribe (Messages)
            }, // end of Initialize().Subscribe onNext
            ex => logger.LogError(ex, "[ThreadExec] Initialize failed for {ThreadPath}", threadPath));

        // Register subscription for disposal
        workspace.AddDisposable(initSub);

        return delivery.Processed();
    }

    /// <summary>
    /// <summary>
    /// Push streaming content via DataChangeRequest to the response message hub.
    /// One-way message — the response hub updates its own local workspace.
    /// No remote stream sync, no amplification storm.
    /// </summary>
    /// <summary>
    /// Notifies the parent thread that this child thread's execution completed.
    /// The parent's delegation tool handler resolves its TaskCompletionSource.
    /// Only posts if this thread IS a child (path has a parent response message segment).
    /// </summary>
    private static void NotifyParentCompletion(
        IMessageHub hub, string threadPath, string responseText, bool success,
        ImmutableList<NodeChangeEntry>? updatedNodes = null)
    {
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<AgentChatClient>>();
        var status = success ? SubmitMessageStatus.ExecutionCompleted
            : SubmitMessageStatus.ExecutionFailed;
        logger.LogInformation("[ThreadExec] NOTIFY_PARENT: threadPath={ThreadPath}, status={Status}, textLen={TextLen}",
            threadPath, status, responseText.Length);

        // Invoke the completion callback registered by HandleSubmitMessage.
        // This posts a SubmitMessageResponse(ExecutionCompleted) via ResponseFor(originalDelivery)
        // on the thread hub, which routes back to the client's RegisterCallback.
        if (CompletionCallbacks.TryGetValue(threadPath, out var callback))
        {
            callback(new SubmitMessageResponse
            {
                Success = success,
                Status = status,
                ResponseText = Truncate(responseText, 500),
                UpdatedNodes = updatedNodes
            });
        }
        else
        {
            logger.LogWarning("[ThreadExec] No completion callback for {ThreadPath}", threadPath);
        }
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

    private static IMessageDelivery HandleCancelStream(
        IMessageHub hub, IMessageDelivery<CancelThreadStreamRequest> delivery)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        var threadPath = hub.Address.Path;

        // Read Thread.StreamingToolCalls from workspace (runs on grain scheduler — safe).
        // Find active delegation sub-threads and propagate cancel via Post (fire-and-forget).
        hub.GetWorkspace().UpdateMeshNode(node =>
        {
            var thread = node.Content as MeshThread;
            if (thread?.StreamingToolCalls is { Count: > 0 })
            {
                foreach (var tc in thread.StreamingToolCalls.Where(
                    tc => !string.IsNullOrEmpty(tc.DelegationPath) && tc.Result == null))
                {
                    logger?.LogInformation("[ThreadExec] Propagating cancel to sub-thread {SubThread}", tc.DelegationPath);
                    hub.Post(new CancelThreadStreamRequest { ThreadPath = tc.DelegationPath! },
                        o => o.WithTarget(new Address(tc.DelegationPath!)));
                }
            }
            return node; // No state change needed
        });

        // Cancel own execution via CancellationTokenSource (streaming runs on thread pool)
        if (ExecutionCancellations.TryGetValue(threadPath, out var cts))
        {
            logger?.LogInformation("[ThreadExec] Cancelling own execution for {ThreadPath}", threadPath);
            cts.Cancel();
        }

        // Post response so parent can await confirmation
        hub.Post(new CancelThreadStreamResponse { ThreadPath = threadPath },
            o => o.ResponseFor(delivery));

        return delivery.Processed();
    }
}
