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

            // 1. Mark the active response message as Cancelled
            if (!string.IsNullOrEmpty(thread.ActiveMessageId))
            {
                var responsePath = $"{threadPath}/{thread.ActiveMessageId}";
                var responseStream = workspace.GetRemoteStream<MeshNode>(
                    new Address(responsePath), new MeshNodeReference());

                responseStream.Update(current =>
                {
                    if (current == null) return null;
                    var msg = current.Content as ThreadMessage;
                    var lastUpdated = current.LastModified != default
                        ? current.LastModified.ToString("HH:mm:ss")
                        : msg?.Timestamp.ToString("HH:mm:ss") ?? "unknown";
                    var updated = current with
                    {
                        Content = new ThreadMessage
                        {
                            Id = thread.ActiveMessageId,
                            Role = "assistant",
                            Text = (msg?.Text ?? "") + $"\n\n*Cancelled (last updated {lastUpdated})*",
                            Timestamp = msg?.Timestamp ?? DateTime.UtcNow,
                            Type = ThreadMessageType.AgentResponse,
                            AgentName = msg?.AgentName,
                            ModelName = msg?.ModelName,
                            ToolCalls = msg?.ToolCalls ?? []
                        }
                    };
                    return new ChangeItem<MeshNode>(updated, responseStream.StreamId,
                        responseStream.StreamId, ChangeType.Patch, responseStream.Hub.Version,
                        [new EntityUpdate(nameof(MeshNode), responsePath, updated) { OldValue = current }]);
                });
            }

            // 2. Clear thread execution state, mark progress as completed
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
                        ExecutionStatus = $"Cancelled at {cancelledAt:HH:mm:ss}",
                        ActiveMessageId = null,
                        TokensUsed = 0,
                        ExecutionStartedAt = null
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

        // Capture workspace BEFORE Subscribe — inside Subscribe callback, hub context may differ
        var threadWorkspace = hub.GetWorkspace();

        // MainNode = content entity (e.g., "PartnerRe/AiConsulting"), not a thread path.
        // This is critical for access control — all satellite nodes must reference the main entity.
        var mainEntity = request.ContextPath ?? threadPath;

        // 1) Create both cells concurrently via Observable.
        //    CreateNode captures AccessContext eagerly at call time (inside the delivery
        //    pipeline where it's set from delivery.AccessContext). No explicit identity needed.
        var inputObs = meshService.CreateNode(new MeshNode(userMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = mainEntity,
            Content = new ThreadMessage
            {
                Id = userMsgId,
                Role = "user",
                Text = request.UserMessageText,
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput,
                CreatedBy = delivery.AccessContext?.ObjectId
            }
        });

        var outputObs = meshService.CreateNode(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = mainEntity,
            Content = new ThreadMessage
            {
                Id = responseMsgId,
                Role = "assistant",
                Text = "",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse,
                AgentName = request.AgentName,
                ModelName = request.ModelName
            }
        });

        inputObs.Zip(outputObs).Subscribe(
            pair =>
            {
                logger?.LogInformation("HandleSubmitMessage: cells created for {ThreadPath}", threadPath);

                // 2) Update Thread: add message IDs and set execution state
                threadWorkspace.UpdateMeshNode(node =>
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

                // 3) Start execution on hosted hub — forward user's AccessContext
                var executionHub = hub.GetHostedHub(
                    new Address($"{hub.Address}/_Exec"),
                    config => config.WithHandler<SubmitMessageRequest>(ExecuteMessageAsync),
                    HostedHubCreation.Always);

                executionHub!.Post(request with { ResponsePath = responsePath },
                    o => delivery.AccessContext != null ? o.WithAccessContext(delivery.AccessContext) : o);

                // 4) Response — nodes created successfully
                hub.Post(new SubmitMessageResponse { Success = true }, o => o.ResponseFor(delivery));
            },
            error =>
            {
                var errorMsg = error.Message ?? "Node creation failed";
                logger?.LogError(error, "HandleSubmitMessage: node creation failed for {ThreadPath}: {Error}",
                    threadPath, errorMsg);
                hub.Post(new SubmitMessageResponse { Success = false, Error = errorMsg },
                    o => o.ResponseFor(delivery));
            });
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
    private static IMessageDelivery ExecuteMessageAsync(
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

        // Get remote stream ONCE for the response node
        var responseStream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(responsePath), new MeshNodeReference());

        // Helper: update Thread execution state
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

                var toolCallLog = new List<ToolCallEntry>();
                string? currentStatus = null;
                client.UpdateDelegationStatus = status => { currentStatus = status; };
                client.ForwardToolCall = entry => { toolCallLog.Add(entry); };

                var agentDisplayName = request.AgentName ?? "Agent";

                // Run AI streaming via InvokeAsync — external I/O, not Orleans
                var chatMessage = new ChatMessage(ChatRole.User, request.UserMessageText);
                logger.LogInformation("[ThreadExec] Sending to agent: threadPath={ThreadPath}, agent={Agent}, model={Model}, msgLength={Length}",
                    threadPath, request.AgentName ?? "(default)", request.ModelName ?? "(default)", request.UserMessageText?.Length ?? 0);
                string? firstDelegationPath = null;

                hub.InvokeAsync(async ct =>
                {
                    try
                    {
                    var responseText = new StringBuilder();
                    var lastUpdate = DateTimeOffset.MinValue;
                    var lastStatusUpdate = DateTimeOffset.MinValue;
                    var pendingCalls = new Dictionary<string, FunctionCallContent>();
                    string? lastCallKey = null;
                    var lastPushedToolCallCount = 0;

                    await foreach (var update in client.GetStreamingResponseAsync([chatMessage], ct))
            {
                // Capture function call / delegation activity for execution status
                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent functionCall)
                    {
                        logger.LogDebug("[ThreadExec] Tool call: {Name}({Args})", functionCall.Name, SerializeArgs(functionCall.Arguments));
                        // Build detailed status: formatted name + arguments
                        var formatted = ToolStatusFormatter.Format(functionCall);
                        var argsDetail = SerializeArgs(functionCall.Arguments);
                        currentStatus = argsDetail != null
                            ? $"{formatted}\n{argsDetail}"
                            : formatted;

                        // Track pending call — use CallId if available, fall back to Name+counter
                        var callKey = functionCall.CallId ?? $"{functionCall.Name}_{pendingCalls.Count}";
                        pendingCalls[callKey] = functionCall;
                        lastCallKey = callKey;
                    }
                    else if (content is FunctionResultContent functionResult)
                    {
                        logger.LogDebug("[ThreadExec] Tool result: {Name}, success={Success}, length={Length}",
                            functionResult.CallId, functionResult.Result?.ToString()?.StartsWith("Error") != true,
                            functionResult.Result?.ToString()?.Length ?? 0);
                        // Match result to pending call — try CallId first, then last pending call
                        var resultKey = functionResult.CallId ?? lastCallKey;
                        FunctionCallContent? originalCall = null;
                        if (resultKey != null)
                            pendingCalls.Remove(resultKey, out originalCall);
                        originalCall ??= pendingCalls.Values.LastOrDefault();

                        if (originalCall != null)
                        {
                            string? delegationPath = null;
                            if (originalCall.Name.StartsWith("delegate_to"))
                            {
                                delegationPath = chatClient.LastDelegationPath;
                                chatClient.LastDelegationPath = null;
                                firstDelegationPath ??= delegationPath;
                            }

                            toolCallLog.Add(new ToolCallEntry
                            {
                                Name = originalCall.Name,
                                DisplayName = ToolStatusFormatter.Format(originalCall),
                                Arguments = SerializeArgs(originalCall.Arguments),
                                Result = Truncate(functionResult.Result?.ToString()),
                                IsSuccess = functionResult.Result?.ToString()?.StartsWith("Error") != true,
                                DelegationPath = delegationPath,
                                Timestamp = DateTime.UtcNow
                            });
                        }
                        currentStatus = null; // Tool call completed
                    }
                }

                // Update execution status when status or tool calls change (throttled)
                if ((currentStatus != null || toolCallLog.Count > lastPushedToolCallCount) && responseText.Length == 0
                    && DateTimeOffset.UtcNow - lastStatusUpdate > TimeSpan.FromMilliseconds(300))
                {
                    var status = currentStatus ?? "";
                    var liveToolCalls = toolCallLog.ToImmutableList();
                    lastPushedToolCallCount = liveToolCalls.Count;
                    responseStream.Update(current =>
                    {
                        if (current == null) return null;
                        var msg = current.Content as ThreadMessage;
                        var updated = current with
                        {
                            Content = new ThreadMessage
                            {
                                Id = responseMsgId,
                                Role = "assistant",
                                Text = "",
                                Timestamp = msg?.Timestamp ?? DateTime.UtcNow,
                                Type = ThreadMessageType.AgentResponse,
                                AgentName = request.AgentName,
                                ModelName = request.ModelName,
                                ToolCalls = liveToolCalls
                            }
                        };
                        return new ChangeItem<MeshNode>(updated, responseStream.StreamId,
                            responseStream.StreamId, ChangeType.Patch, responseStream.Hub.Version,
                            [new EntityUpdate(nameof(MeshNode), responsePath, updated) { OldValue = current }]);
                    });
                    // Push status + live tool calls to Thread node (read by ToolCallsView from workspace stream)
                    UpdateThreadExecution(t => t with { ExecutionStatus = status, ActiveToolCalls = liveToolCalls });
                    lastStatusUpdate = DateTimeOffset.UtcNow;
                }

                if (!string.IsNullOrEmpty(update.Text))
                {
                    responseText.Append(update.Text);

                    if (DateTimeOffset.UtcNow - lastUpdate > TimeSpan.FromMilliseconds(200))
                    {
                        var text = responseText.ToString();
                        responseStream.Update(current =>
                        {
                            if (current == null) return null;
                            var updated = current with
                            {
                                Content = new ThreadMessage
                                {
                                    Id = responseMsgId,
                                    Role = "assistant",
                                    Text = text,
                                    Timestamp = DateTime.UtcNow,
                                    Type = ThreadMessageType.AgentResponse
                                }
                            };
                            return new ChangeItem<MeshNode>(updated, responseStream.StreamId,
                                responseStream.StreamId, ChangeType.Patch, responseStream.Hub.Version,
                                [new EntityUpdate(nameof(MeshNode), responsePath, updated) { OldValue = current }]);
                        });
                        lastUpdate = DateTimeOffset.UtcNow;
                    }
                }
            }

                    // Final update — mark execution as complete
                    logger.LogInformation("[ThreadExec] Execution completed: threadPath={ThreadPath}, responseLength={Length}, toolCalls={ToolCalls}",
                        threadPath, responseText.Length, toolCallLog.Count);
                    var finalText = responseText.ToString();
                    var finalToolCalls = toolCallLog.ToImmutableList();
                    responseStream.Update(current =>
                    {
                        if (current == null) return null;
                        var updated = current with
                        {
                            Content = new ThreadMessage
                            {
                                Id = responseMsgId, Role = "assistant", Text = finalText,
                                Timestamp = DateTime.UtcNow, Type = ThreadMessageType.AgentResponse,
                                AgentName = request.AgentName, ModelName = request.ModelName,
                                ToolCalls = finalToolCalls, DelegationPath = firstDelegationPath
                            }
                        };
                        return new ChangeItem<MeshNode>(updated, responseStream.StreamId,
                            responseStream.StreamId, ChangeType.Patch, responseStream.Hub.Version,
                            [new EntityUpdate(nameof(MeshNode), responsePath, updated) { OldValue = current }]);
                    });

                    UpdateThreadExecution(t => t with
                    {
                        IsExecuting = false, ExecutionStatus = null, ActiveMessageId = null, ActiveToolCalls = [],
                        ExecutionStartedAt = null                    });
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogInformation("[ThreadExec] Cancelled: {ThreadPath}", threadPath);
                        MarkResponseCancelled(responseStream, responseMsgId, request, responsePath, toolCallLog, firstDelegationPath);
                        UpdateThreadExecution(t => t with
                        {
                            IsExecuting = false, ExecutionStatus = null, ActiveMessageId = null, ActiveToolCalls = [],
                            ExecutionStartedAt = null                        });
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "[ThreadExec] Error: {ThreadPath}", threadPath);
                        MarkResponseError(responseStream, responseMsgId, request, responsePath, ex, toolCallLog, firstDelegationPath);
                        UpdateThreadExecution(t => t with
                        {
                            IsExecuting = false, ExecutionStatus = null, ActiveMessageId = null, ActiveToolCalls = [],
                            ExecutionStartedAt = null                        });
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
                var history = new List<ThreadMessage>();
                foreach (var msgId in threadContent.Messages)
                {
                    if (msgId == responseMsgId) continue;
                    var msgStream = workspace.GetRemoteStream<MeshNode>(
                        new Address($"{threadPath}/{msgId}"), new MeshNodeReference());
                    var msgNode = msgStream.Current?.Value;
                    if (msgNode?.Content is ThreadMessage tmsg && tmsg.Type != ThreadMessageType.EditingPrompt)
                        history.Add(tmsg);
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

    private static void MarkResponseCancelled(
        ISynchronizationStream<MeshNode> responseStream, string responseMsgId,
        SubmitMessageRequest request, string responsePath,
        List<ToolCallEntry> toolCallLog, string? delegationPath)
    {
        responseStream.Update(current =>
        {
            if (current == null) return null;
            var msg = current.Content as ThreadMessage;
            var updated = current with
            {
                Content = new ThreadMessage
                {
                    Id = responseMsgId, Role = "assistant",
                    Text = (msg?.Text ?? "") + "\n\n*Cancelled*",
                    Timestamp = DateTime.UtcNow, Type = ThreadMessageType.AgentResponse,
                    AgentName = request.AgentName, ModelName = request.ModelName,
                    ToolCalls = toolCallLog.ToImmutableList(), DelegationPath = delegationPath
                }
            };
            return new ChangeItem<MeshNode>(updated, responseStream.StreamId,
                responseStream.StreamId, ChangeType.Patch, responseStream.Hub.Version,
                [new EntityUpdate(nameof(MeshNode), responsePath, updated) { OldValue = current }]);
        });
    }

    private static void MarkResponseError(
        ISynchronizationStream<MeshNode> responseStream, string responseMsgId,
        SubmitMessageRequest request, string responsePath,
        Exception ex, List<ToolCallEntry> toolCallLog, string? delegationPath)
    {
        responseStream.Update(current =>
        {
            if (current == null) return null;
            var msg = current.Content as ThreadMessage;
            var updated = current with
            {
                Content = new ThreadMessage
                {
                    Id = responseMsgId, Role = "assistant",
                    Text = (msg?.Text ?? "") + $"\n\n*Error: {ex.Message}*",
                    Timestamp = DateTime.UtcNow, Type = ThreadMessageType.AgentResponse,
                    AgentName = request.AgentName, ModelName = request.ModelName,
                    ToolCalls = toolCallLog.ToImmutableList(), DelegationPath = delegationPath
                }
            };
            return new ChangeItem<MeshNode>(updated, responseStream.StreamId,
                responseStream.StreamId, ChangeType.Patch, responseStream.Hub.Version,
                [new EntityUpdate(nameof(MeshNode), responsePath, updated) { OldValue = current }]);
        });
    }

    private static string? SerializeArgs(IDictionary<string, object?>? args)
    {
        if (args == null || args.Count == 0)
            return null;
        try
        {
            // Format as readable key=value pairs instead of raw JSON
            var parts = new List<string>();
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
                parts.Add($"{key}: {valStr}");
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

    private static IMessageDelivery HandleCancelStream(
        IMessageHub hub, IMessageDelivery<CancelThreadStreamRequest> delivery)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        var threadPath = hub.Address.Path;

        // Bottom-up cancellation: cancel sub-threads first (via request/response), then own execution.
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService != null)
        {
            hub.InvokeAsync(async ct =>
            {
                try
                {
                    // Find active sub-threads via Thread.ActiveMessageId → DelegationPath
                    var threadNode = await meshService.QueryAsync<MeshNode>($"path:{threadPath}", ct: ct)
                        .FirstOrDefaultAsync(ct);
                    var thread = threadNode?.Content as MeshThread;

                    if (thread is { IsExecuting: true, ActiveMessageId: { } activeMsgId })
                    {
                        var activeMsgPath = $"{threadPath}/{activeMsgId}";
                        var activeMsg = await meshService.QueryAsync<MeshNode>($"path:{activeMsgPath}", ct: ct)
                            .FirstOrDefaultAsync(ct);

                        if (activeMsg?.Content is ThreadMessage { DelegationPath: { Length: > 0 } delegationPath })
                        {
                            logger?.LogInformation("[ThreadExec] Propagating cancel to sub-thread {SubThread}", delegationPath);
                            var cancelDelivery = hub.Post(new CancelThreadStreamRequest { ThreadPath = delegationPath },
                                o => o.WithTarget(new Address(delegationPath)));
                            if (cancelDelivery != null)
                            {
                                try
                                {
                                    await hub.RegisterCallback(cancelDelivery, (d, _) => Task.FromResult(d), ct)
                                        .WaitAsync(TimeSpan.FromSeconds(10), ct);
                                }
                                catch (TimeoutException)
                                {
                                    logger?.LogWarning("[ThreadExec] Sub-thread cancel timed out for {SubThread}", delegationPath);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "[ThreadExec] Error propagating cancellation for {ThreadPath}", threadPath);
                }

                // Cancel own execution (after sub-threads confirmed)
                var execHub = hub.GetHostedHub(new Address($"{hub.Address}/_Exec"), HostedHubCreation.Never);
                if (execHub != null)
                {
                    logger?.LogInformation("[ThreadExec] Cancelling own execution for {ThreadPath}", threadPath);
                    execHub.CancelCurrentExecution();
                }
            }, ex =>
            {
                logger?.LogWarning(ex, "[ThreadExec] Error during cancel for {ThreadPath}", threadPath);
                var execHub = hub.GetHostedHub(new Address($"{hub.Address}/_Exec"), HostedHubCreation.Never);
                execHub?.CancelCurrentExecution();
                return Task.CompletedTask;
            });
        }
        else
        {
            // No mesh service — just cancel own execution
            var execHub = hub.GetHostedHub(new Address($"{hub.Address}/_Exec"), HostedHubCreation.Never);
            execHub?.CancelCurrentExecution();
        }

        // Post response so parent can await confirmation
        hub.Post(new CancelThreadStreamResponse { ThreadPath = threadPath },
            o => o.ResponseFor(delivery));

        return delivery.Processed();
    }
}
