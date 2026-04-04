using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
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

    public static MessageHubConfiguration AddThreadExecution(this MessageHubConfiguration configuration)
        => configuration
            .WithHandler<SubmitMessageRequest>(HandleSubmitMessage)
            .WithHandler<CancelThreadStreamRequest>(HandleCancelStream)
            .WithInitialization(RecoverStaleExecutingThread);

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
    /// Handles SubmitMessageRequest on the thread hub.
    /// 1) Create input + output cells concurrently
    /// 2) On success: update Thread.ThreadMessages via stream, start execution
    /// 3) On failure: respond with error
    /// </summary>
    internal static IMessageDelivery HandleSubmitMessage(
        IMessageHub hub,
        IMessageDelivery<SubmitMessageRequest> delivery)
    {
        var request = delivery.Message;
        var threadPath = request.ThreadPath;
        var logger = hub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        logger?.LogDebug("[ThreadExec] HandleSubmitMessage: threadPath={ThreadPath}, user={User}, hubAddress={Hub}",
            threadPath, request.ContextPath, hub.Address);
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        var userMsgId = Guid.NewGuid().ToString("N")[..8];
        var responseMsgId = Guid.NewGuid().ToString("N")[..8];
        var responsePath = $"{threadPath}/{responseMsgId}";
        logger?.LogDebug("[ThreadExec] Creating cells: userMsg={UserMsgId}, responseMsg={ResponseMsgId}",
            userMsgId, responseMsgId);

        var mainEntity = request.ContextPath ?? threadPath;

        // 1) Create both cells — fire and forget (no callback).
        //    Node grains activate immediately; content will be available by the time execution streams.
        meshService.CreateNode(new MeshNode(userMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = mainEntity,
            Content = new ThreadMessage
            {
                Role = "user",
                Text = request.UserMessageText,
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput,
                CreatedBy = delivery.AccessContext?.ObjectId
            }
        }).Subscribe(
            _ => logger?.LogInformation("HandleSubmitMessage: user cell created for {ThreadPath}", threadPath),
            ex => logger?.LogError(ex, "HandleSubmitMessage: user cell creation failed for {ThreadPath}", threadPath));

        meshService.CreateNode(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = mainEntity,
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse,
                AgentName = request.AgentName,
                ModelName = request.ModelName
            }
        }).Subscribe(
            _ => logger?.LogInformation("HandleSubmitMessage: response cell created for {ThreadPath}", threadPath),
            ex => logger?.LogError(ex, "HandleSubmitMessage: response cell creation failed for {ThreadPath}", threadPath));

        // 2) Update Thread on the thread hub — runs on grain scheduler, safe.
        //    Don't wait for CreateNode: grains activate on first message.
        hub.GetWorkspace().UpdateMeshNode(node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            return node with
            {
                Content = thread with
                {
                    Messages = thread.Messages.AddRange([userMsgId, responseMsgId]),
                    IsExecuting = true,
                    ActiveMessageId = responseMsgId,
                    ExecutionStatus = null,
                    TokensUsed = 0,
                    ExecutionStartedAt = DateTime.UtcNow
                }
            };
        });

        // 3) Register completion callback so _Exec can notify original client
        CompletionCallbacks[threadPath] = completionResponse =>
        {
            logger?.LogInformation("[ThreadExec] Forwarding {Status} to client for {ThreadPath}",
                completionResponse.Status, threadPath);
            hub.Post(completionResponse, o => o.ResponseFor(delivery));
            CompletionCallbacks.TryRemove(threadPath, out _);
        };

        // 4) Start execution on hosted hub — forward user's AccessContext
        var executionHub = hub.GetHostedHub(
            new Address($"{hub.Address}/_Exec"),
            config => config.WithHandler<SubmitMessageRequest>(ExecuteMessageAsync),
            HostedHubCreation.Always);

        executionHub!.Post(request with { ResponsePath = responsePath },
            o => delivery.AccessContext != null ? o.WithAccessContext(delivery.AccessContext) : o);

        // 5) Response — cells creation initiated
        hub.Post(new SubmitMessageResponse { Success = true }, o => o.ResponseFor(delivery));

        return delivery.Processed();
    }

    /// <summary>
    /// Async handler on the _Exec hosted hub.
    /// Prepares agent and await-streams the response.
    /// Uses UpdateMeshNode on a remote stream to push text to the response node.
    /// </summary>
    /// <summary>
    /// Fully reactive execution handler — zero await, zero QueryAsync.
    /// Subscribes to chatClient.Initialize() observable, then runs streaming in the callback.
    /// The AI API streaming (GetStreamingResponseAsync) runs via hub.InvokeAsync for async I/O.
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
        // Posts UpdateThreadMessageContent which is handled ON the grain —
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

        // Initialize agent — returns IObservable<AgentChatClient>
        var chatClient = new AgentChatClient(parentHub.ServiceProvider);
        chatClient.SetThreadId(threadPath);

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

                // Load history from workspace stream
                LoadHistoryFromStream(client, threadWorkspace, workspace, threadPath, responseMsgId, logger);

                // Set execution context
                var userAccessContext = delivery.AccessContext;
                client.SetExecutionContext(new ThreadExecutionContext
                {
                    ThreadPath = threadPath,
                    ResponseMessageId = responseMsgId,
                    ContextPath = request.ContextPath,
                    UserAccessContext = userAccessContext
                });

                var toolCallLog = ImmutableList<ToolCallEntry>.Empty;
                var nodeChangeLog = ImmutableList<NodeChangeEntry>.Empty;
                // responseText is captured after InvokeAsync creates it (see below)
                StringBuilder? capturedResponseText = null;
                client.ForwardNodeChange = entry => { nodeChangeLog = nodeChangeLog.Add(entry); };
                string? currentStatus = null;
                client.UpdateDelegationStatus = status =>
                {
                    currentStatus = status;
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

                // Run AI streaming via InvokeAsync — external I/O, not Orleans
                var chatMessage = new ChatMessage(ChatRole.User, request.UserMessageText);
                logger.LogInformation("[ThreadExec] Sending to agent: threadPath={ThreadPath}, agent={Agent}, model={Model}, msgLength={Length}",
                    threadPath, request.AgentName ?? "(default)", request.ModelName ?? "(default)", request.UserMessageText?.Length ?? 0);

                logger.LogInformation("[ThreadExec] INVOKE_ASYNC_START: threadPath={ThreadPath}, responsePath={ResponsePath}",
                    threadPath, responsePath);
                hub.InvokeAsync(async ct =>
                {
                    var responseText = new StringBuilder();
                    capturedResponseText = responseText;
                    try
                    {
                    logger.LogInformation("[ThreadExec] STREAMING_LOOP_ENTRY: {Time:HH:mm:ss.fff} threadPath={ThreadPath}", DateTime.UtcNow, threadPath);
                    // Keep the grain alive during the entire execution — including tool calls
                    // and delegations where the streaming loop is blocked.
                    using var heartbeatSubscription = parentHub.BeginAsyncOperation();
                    var lastUpdate = DateTimeOffset.MinValue;
                    var pendingCalls = ImmutableDictionary<string, FunctionCallContent>.Empty;
                    string? lastCallKey = null;

                    await foreach (var update in client.GetStreamingResponseAsync([chatMessage], ct))
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
                        pendingCalls = pendingCalls.SetItem(callKey, functionCall);
                        lastCallKey = callKey;

                        // Add pending tool call to local log — will be pushed on next throttled update
                        toolCallLog = toolCallLog.Add(new ToolCallEntry
                        {
                            Name = functionCall.Name,
                            DisplayName = formatted,
                            Arguments = argsDetail,
                            Timestamp = DateTime.UtcNow
                        });
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
                            if (originalCall.Name.StartsWith("delegate_to"))
                            {
                                // Extract delegation path from the DelegationResult in the tool output.
                                // This is safe for parallel delegations (unlike the old LastDelegationPath
                                // shared state which got overwritten by the last concurrent delegation).
                                delegationPath = ExtractDelegationPath(functionResult.Result?.ToString());
                            }

                            // Replace pending entry with final (has Result + DelegationPath)
                            var finalEntry = new ToolCallEntry
                            {
                                Name = originalCall.Name,
                                DisplayName = ToolStatusFormatter.Format(originalCall),
                                Arguments = SerializeArgs(originalCall.Arguments),
                                Result = Truncate(functionResult.Result?.ToString()),
                                IsSuccess = functionResult.Result?.ToString()?.StartsWith("Error") != true,
                                DelegationPath = delegationPath,
                                Timestamp = DateTime.UtcNow
                            };
                            var idx = toolCallLog.FindIndex(e => e.Name == originalCall.Name && e.Result == null);
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

                // Push streaming content at ~1/sec via responseStream.Update
                if (DateTimeOffset.UtcNow - lastUpdate > TimeSpan.FromMilliseconds(1000))
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

                    // Final update — aggregate node changes (merges sub-thread changes with min/max versions)
                    var aggregatedChanges = AggregateNodeChanges(nodeChangeLog);
                    logger.LogInformation("[ThreadExec] EXECUTION_COMPLETE: {Time:HH:mm:ss.fff} threadPath={ThreadPath}, responseLength={Length}, toolCalls={ToolCalls}",
                        DateTime.UtcNow, threadPath, responseText.Length, toolCallLog.Count);
                    var finalText = responseText.ToString();
                    PushToResponseMessage(finalText, toolCallLog, aggregatedChanges,
                        request.AgentName, request.ModelName);
                    // Clear streaming state from Thread
                    UpdateThreadExecution(t => t with
                    {
                        IsExecuting = false, ExecutionStatus = null, ActiveMessageId = null,
                        ExecutionStartedAt = null, StreamingText = null, StreamingToolCalls = null
                    });
                    // Notify parent via SubmitMessageResponse so delegation callback resolves.
                    // Must post on the _Exec hub (hub) — the SubmitMessageResponse handler
                    // is registered there and forwards to the thread hub via ResponseFor.
                    NotifyParentCompletion(hub, threadPath, finalText, true, delivery, aggregatedChanges);
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
                        NotifyParentCompletion(hub, threadPath, cancelText, false, delivery, nodeChangeLog);
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
                        NotifyParentCompletion(hub, threadPath, errorText, false, delivery, nodeChangeLog);
                    }
                }, ex =>
                {
                    logger.LogError(ex, "[ThreadExec] InvokeAsync error: {ThreadPath}", threadPath);
                    return Task.CompletedTask;
                });
            }, // end of Initialize().Subscribe onNext
            ex => logger.LogError(ex, "[ThreadExec] Initialize failed for {ThreadPath}", threadPath));

        // Register subscription for disposal
        workspace.AddDisposable(initSub);

        return delivery.Processed();
    }

    private static void LoadHistoryFromStream(
        AgentChatClient client, IWorkspace threadWorkspace, IWorkspace workspace,
        string threadPath, string responseMsgId, ILogger logger)
    {
        try
        {
            var threadStream = threadWorkspace.GetStream(new MeshNodeReference());
            var threadNode = threadStream?.Current?.Value;
            var threadContent = threadNode?.Content as AI.Thread;
            if (threadContent?.Messages.Count > 0)
            {
                var history = ImmutableList<ThreadMessage>.Empty;
                foreach (var msgId in threadContent.Messages)
                {
                    if (msgId == responseMsgId) continue;
                    var msgStream = workspace.GetRemoteStream<MeshNode>(
                        new Address($"{threadPath}/{msgId}"), new MeshNodeReference());
                    var msgNode = msgStream.Current?.Value;
                    if (msgNode?.Content is ThreadMessage tmsg && tmsg.Type != ThreadMessageType.EditingPrompt)
                        history = history.Add(tmsg);
                }
                if (history.Count > 0)
                {
                    client.SetConversationHistory(history);
                    logger.LogInformation("[ThreadExec] Loaded {Count} history messages for {ThreadPath}",
                        history.Count, threadPath);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load conversation history for {ThreadPath}", threadPath);
        }
    }

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
        IMessageDelivery<SubmitMessageRequest> delivery,
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
    /// Extracts the delegation path (ThreadId) from a serialized DelegationResult in tool output.
    /// This is parallel-safe — each tool result carries its own thread path.
    /// </summary>
    private static string? ExtractDelegationPath(string? toolResult)
    {
        if (string.IsNullOrEmpty(toolResult))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(toolResult);
            if (doc.RootElement.TryGetProperty("threadId", out var threadIdProp) ||
                doc.RootElement.TryGetProperty("ThreadId", out threadIdProp))
            {
                return threadIdProp.GetString();
            }
        }
        catch
        {
            // Not JSON or no ThreadId — fall back to null
        }
        return null;
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

        // Cancel own execution
        var execHub = hub.GetHostedHub(new Address($"{hub.Address}/_Exec"), HostedHubCreation.Never);
        if (execHub != null)
        {
            logger?.LogInformation("[ThreadExec] Cancelling own execution for {ThreadPath}", threadPath);
            execHub.CancelCurrentExecution();
        }

        // Post response so parent can await confirmation
        hub.Post(new CancelThreadStreamResponse { ThreadPath = threadPath },
            o => o.ResponseFor(delivery));

        return delivery.Processed();
    }
}
