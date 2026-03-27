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
            .WithInitialization(RecoverStaleExecutingCells);

    /// <summary>
    /// On hub startup, find any ThreadMessage nodes that are still marked as IsExecuting=true.
    /// These are from crashed/restarted sessions. Mark them as cancelled so the UI shows the correct state.
    /// </summary>
    private static async Task RecoverStaleExecutingCells(IMessageHub hub, CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService == null) return;

        var threadPath = hub.Address.Path;
        try
        {
            // Recover stale executing cells in this thread's direct children
            await RecoverNamespaceAsync(meshService, threadPath, logger, ct);

            // Also recover in sub-threads: scan message nodes for Thread children
            await foreach (var msgNode in meshService.QueryAsync<MeshNode>(
                $"namespace:{threadPath} nodeType:{ThreadMessageNodeType.NodeType}"))
            {
                // Check sub-threads under each message
                await foreach (var subThread in meshService.QueryAsync<MeshNode>(
                    $"namespace:{msgNode.Path} nodeType:{ThreadNodeType.NodeType}"))
                {
                    await RecoverNamespaceAsync(meshService, subThread.Path, logger, ct);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[ThreadExec] Recovery failed for {ThreadPath}", threadPath);
        }
    }

    private static async Task RecoverNamespaceAsync(
        IMeshService meshService, string namespacePath, ILogger? logger, CancellationToken ct)
    {
        await foreach (var node in meshService.QueryAsync<MeshNode>(
            $"namespace:{namespacePath} nodeType:{ThreadMessageNodeType.NodeType}"))
        {
            if (node.Content is ThreadMessage { IsExecuting: true } tmsg)
            {
                logger?.LogInformation("[ThreadExec] Recovery: marking stale executing cell {MsgId} as crashed in {Path}",
                    tmsg.Id, namespacePath);

                var recovered = node with
                {
                    Content = tmsg with
                    {
                        IsExecuting = false,
                        ExecutionStatus = null,
                        Text = (tmsg.Text ?? "") + (string.IsNullOrEmpty(tmsg.Text) ? "*Interrupted — session restarted*" : "\n\n*Interrupted — session restarted*")
                    }
                };
                await meshService.UpdateNodeAsync(recovered, ct);
            }
        }
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

        // 1) Create both cells concurrently via Observable.
        //    CreateNode captures AccessContext eagerly at call time (inside the delivery
        //    pipeline where it's set from delivery.AccessContext). No explicit identity needed.
        var inputObs = meshService.CreateNode(new MeshNode(userMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
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
            Content = new ThreadMessage
            {
                Id = responseMsgId,
                Role = "assistant",
                Text = "",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse,
                AgentName = request.AgentName,
                ModelName = request.ModelName,
                IsExecuting = true
            }
        });

        inputObs.Zip(outputObs).Subscribe(
            pair =>
            {
                logger?.LogInformation("HandleSubmitMessage: cells created for {ThreadPath}", threadPath);

                // 2) Update Thread.Messages via workspace
                threadWorkspace.UpdateMeshNode(node =>
                {
                    var thread = node.Content as MeshThread ?? new MeshThread();
                    return node with
                    {
                        Content = thread with
                        {
                            Messages = thread.Messages.AddRange([userMsgId, responseMsgId])
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
    private static async Task<IMessageDelivery> ExecuteMessageAsync(
        IMessageHub hub,
        IMessageDelivery<SubmitMessageRequest> delivery,
        CancellationToken ct)
    {
        var request = delivery.Message;
        var parentHub = hub.Configuration.ParentHub!;
        var threadPath = request.ThreadPath;
        var responsePath = request.ResponsePath!;
        var responseMsgId = responsePath.Split('/').Last();
        var logger = parentHub.ServiceProvider.GetRequiredService<ILogger<AgentChatClient>>();
        var workspace = parentHub.ServiceProvider.GetRequiredService<IWorkspace>();

        // Get remote stream ONCE for the response node — declared outside try for catch access
        var responseStream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(responsePath), new MeshNodeReference());

        // Declared outside try so catch blocks can include partial results
        var toolCallLog = new List<ToolCallEntry>();
        string? firstDelegationPath = null;

        try
        {
            // Set user access context for the entire execution scope
            // so all tool calls (Create, Update, etc.) run under the correct user identity
            var accessService = parentHub.ServiceProvider.GetService<AccessService>();
            if (delivery.AccessContext != null)
                accessService?.SetContext(delivery.AccessContext);

            // 1. Prepare agent
            logger.LogDebug("[ThreadExec] ExecuteMessageAsync: preparing agent for {ThreadPath}, responsePath={ResponsePath}",
                threadPath, responsePath);
            var chatClient = new AgentChatClient(parentHub.ServiceProvider);
            chatClient.SetThreadId(threadPath);
            await chatClient.InitializeAsync(request.ContextPath, request.ModelName);
            logger.LogDebug("[ThreadExec] ExecuteMessageAsync: agent initialized for {ThreadPath}", threadPath);

            // Set application context so the agent knows which node it's working on.
            // This populates the "Current Application Context" section in the system prompt,
            // which is critical for sub-threads created via delegation.
            if (!string.IsNullOrEmpty(request.ContextPath))
            {
                var meshService2 = parentHub.ServiceProvider.GetRequiredService<IMeshService>();
                var contextNode = await meshService2.QueryAsync<MeshNode>(
                    $"path:{request.ContextPath}").FirstOrDefaultAsync(ct);
                chatClient.SetContext(new AgentContext
                {
                    Address = new Address(request.ContextPath),
                    Context = request.ContextPath,
                    Node = contextNode
                });
            }

            if (!string.IsNullOrEmpty(request.AgentName))
                chatClient.SetSelectedAgent(request.AgentName);

            if (request.Attachments is { Count: > 0 })
                chatClient.SetAttachments(request.Attachments);

            // Load conversation history from existing thread messages (resume scenario)
            try
            {
                var meshService = parentHub.ServiceProvider.GetRequiredService<IMeshService>();
                var threadNode = await meshService.QueryAsync<MeshNode>($"path:{threadPath}").FirstOrDefaultAsync(ct);
                var threadContent = threadNode?.Content as AI.Thread;
                if (threadContent?.Messages.Count > 0)
                {
                    var history = new List<ThreadMessage>();
                    foreach (var msgId in threadContent.Messages)
                    {
                        // Skip the current input/output messages (they're being created right now)
                        if (msgId == responseMsgId) continue;
                        await foreach (var msgNode in meshService.QueryAsync<MeshNode>($"path:{threadPath}/{msgId}"))
                        {
                            if (msgNode.Content is ThreadMessage tmsg &&
                                tmsg.Type != ThreadMessageType.EditingPrompt)
                            {
                                history.Add(tmsg);
                            }
                        }
                    }
                    if (history.Count > 0)
                    {
                        chatClient.SetConversationHistory(history);
                        logger.LogInformation("[ThreadExec] Loaded {Count} history messages for {ThreadPath}",
                            history.Count, threadPath);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load conversation history for {ThreadPath}", threadPath);
            }

            // 3. await streaming — update response node via the stream
            var responseText = new StringBuilder();
            var lastUpdate = DateTimeOffset.MinValue;
            var lastStatusUpdate = DateTimeOffset.MinValue;
            string? currentStatus = null;
            var pendingCalls = new Dictionary<string, FunctionCallContent>();
            string? lastCallKey = null;
            var lastPushedToolCallCount = 0;

            // Set execution context for delegation sub-thread creation
            // Capture the user's AccessContext from the delivery so it propagates through delegations
            var userAccessContext = delivery.AccessContext;
            logger.LogInformation("[ThreadExec] ExecuteMessageAsync: user={User}, threadPath={ThreadPath}",
                userAccessContext?.ObjectId ?? "(no-user)", threadPath);
            chatClient.SetExecutionContext(new ThreadExecutionContext
            {
                ThreadPath = threadPath,
                ResponseMessageId = responseMsgId,
                ContextPath = request.ContextPath,
                UserAccessContext = userAccessContext
            });
            chatClient.UpdateDelegationStatus = status => { currentStatus = status; };
            chatClient.ForwardToolCall = entry => { toolCallLog.Add(entry); };
            var chatMessage = new ChatMessage(ChatRole.User, request.UserMessageText);

            await foreach (var update in chatClient.GetStreamingResponseAsync([chatMessage], ct))
            {
                // Capture function call / delegation activity for execution status
                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent functionCall)
                    {
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
                                IsExecuting = true,
                                ExecutionStatus = status,
                                ToolCalls = liveToolCalls
                            }
                        };
                        return new ChangeItem<MeshNode>(updated, responseStream.StreamId,
                            responseStream.StreamId, ChangeType.Patch, responseStream.Hub.Version,
                            [new EntityUpdate(nameof(MeshNode), responsePath, updated) { OldValue = current }]);
                    });
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
                                    Type = ThreadMessageType.AgentResponse,
                                    IsExecuting = true
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

            // 4. Final update — mark execution as complete
            var finalText = responseText.ToString();
            var finalToolCalls = toolCallLog.ToImmutableList();
            responseStream.Update(current =>
            {
                if (current == null) return null;
                var updated = current with
                {
                    Content = new ThreadMessage
                    {
                        Id = responseMsgId,
                        Role = "assistant",
                        Text = finalText,
                        Timestamp = DateTime.UtcNow,
                        Type = ThreadMessageType.AgentResponse,
                        AgentName = request.AgentName,
                        ModelName = request.ModelName,
                        IsExecuting = false,
                        ExecutionStatus = null,
                        ToolCalls = finalToolCalls,
                        DelegationPath = firstDelegationPath
                    }
                };
                return new ChangeItem<MeshNode>(updated, responseStream.StreamId,
                    responseStream.StreamId, ChangeType.Patch, responseStream.Hub.Version,
                    [new EntityUpdate(nameof(MeshNode), responsePath, updated) { OldValue = current }]);
            });
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("ExecuteMessageAsync: cancelled for {ThreadPath}", threadPath);
            // Mark execution as stopped on cancellation
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
                        Text = (msg?.Text ?? "") + "\n\n*Cancelled*",
                        Timestamp = DateTime.UtcNow,
                        Type = ThreadMessageType.AgentResponse,
                        AgentName = request.AgentName,
                        ModelName = request.ModelName,
                        IsExecuting = false,
                        ExecutionStatus = null,
                        ToolCalls = toolCallLog.ToImmutableList(),
                        DelegationPath = firstDelegationPath
                    }
                };
                return new ChangeItem<MeshNode>(updated, responseStream.StreamId,
                    responseStream.StreamId, ChangeType.Patch, responseStream.Hub.Version,
                    [new EntityUpdate(nameof(MeshNode), responsePath, updated) { OldValue = current }]);
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ExecuteMessageAsync: error for {ThreadPath}", threadPath);
            // Mark execution as stopped on error
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
                        Text = (msg?.Text ?? "") + $"\n\n*Error: {ex.Message}*",
                        Timestamp = DateTime.UtcNow,
                        Type = ThreadMessageType.AgentResponse,
                        AgentName = request.AgentName,
                        ModelName = request.ModelName,
                        IsExecuting = false,
                        ExecutionStatus = null,
                        ToolCalls = toolCallLog.ToImmutableList(),
                        DelegationPath = firstDelegationPath
                    }
                };
                return new ChangeItem<MeshNode>(updated, responseStream.StreamId,
                    responseStream.StreamId, ChangeType.Patch, responseStream.Hub.Version,
                    [new EntityUpdate(nameof(MeshNode), responsePath, updated) { OldValue = current }]);
            });
        }

        return delivery.Processed();
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

        // 1. Propagate cancellation to sub-threads first.
        //    Sub-threads are Thread nodes nested under this thread's message nodes.
        //    Pattern: {threadPath}/{msgId}/{subThreadId}
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService != null)
        {
            hub.InvokeAsync(async ct =>
            {
                try
                {
                    // Find executing sub-thread messages (assistant responses still running)
                    await foreach (var node in meshService.QueryAsync<MeshNode>(
                        $"namespace:{threadPath} nodeType:{ThreadMessageNodeType.NodeType}"))
                    {
                        if (node.Content is ThreadMessage { IsExecuting: true, DelegationPath: { Length: > 0 } delegationPath })
                        {
                            logger?.LogInformation("[ThreadExec] Propagating cancel to sub-thread {SubThread}", delegationPath);
                            hub.Post(new CancelThreadStreamRequest { ThreadPath = delegationPath },
                                o => o.WithTarget(new Address(delegationPath)));
                        }
                    }

                    // Also check for sub-threads that are direct children (Thread nodes under message nodes)
                    await foreach (var msgNode in meshService.QueryAsync<MeshNode>(
                        $"namespace:{threadPath}"))
                    {
                        var msgPath = msgNode.Path;
                        await foreach (var subNode in meshService.QueryAsync<MeshNode>(
                            $"namespace:{msgPath} nodeType:{ThreadNodeType.NodeType}"))
                        {
                            // Found a sub-thread — send cancel
                            logger?.LogInformation("[ThreadExec] Propagating cancel to child thread {SubThread}", subNode.Path);
                            hub.Post(new CancelThreadStreamRequest { ThreadPath = subNode.Path },
                                o => o.WithTarget(new Address(subNode.Path)));
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "[ThreadExec] Error propagating cancellation for {ThreadPath}", threadPath);
                }

                // 2. Small delay to let sub-thread cancellation propagate
                await Task.Delay(100, ct);

                // 3. Cancel own execution
                var execHub = hub.GetHostedHub(new Address($"{hub.Address}/_Exec"), HostedHubCreation.Never);
                if (execHub != null)
                {
                    logger?.LogInformation("[ThreadExec] Cancelling own execution for {ThreadPath}", threadPath);
                    execHub.CancelCurrentExecution();
                }
            }, ex =>
            {
                logger?.LogWarning(ex, "[ThreadExec] Error during cancel for {ThreadPath}", threadPath);
                // Still cancel own execution even if sub-thread cancel failed
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

        return delivery.Processed();
    }
}
