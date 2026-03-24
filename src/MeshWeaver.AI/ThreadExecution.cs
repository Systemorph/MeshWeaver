using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
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
    /// </summary>
    public static MessageHubConfiguration AddThreadExecution(this MessageHubConfiguration configuration)
        => configuration
            .WithHandler<SubmitMessageRequest>(HandleSubmitMessage)
            .WithHandler<CancelThreadStreamRequest>(HandleCancelStream);

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

                // 3) Start execution on hosted hub
                var executionHub = hub.GetHostedHub(
                    new Address($"{hub.Address}/_Exec"),
                    config => config.WithHandler<SubmitMessageRequest>(ExecuteMessageAsync),
                    HostedHubCreation.Always);

                executionHub!.Post(request with { ResponsePath = responsePath });

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

        try
        {
            // 1. Prepare agent
            logger.LogDebug("[ThreadExec] ExecuteMessageAsync: preparing agent for {ThreadPath}, responsePath={ResponsePath}",
                threadPath, responsePath);
            var chatClient = new AgentChatClient(parentHub.ServiceProvider);
            chatClient.SetThreadId(threadPath);
            await chatClient.InitializeAsync(request.ContextPath, request.ModelName);
            logger.LogDebug("[ThreadExec] ExecuteMessageAsync: agent initialized for {ThreadPath}", threadPath);

            if (!string.IsNullOrEmpty(request.AgentName))
                chatClient.SetSelectedAgent(request.AgentName);

            if (request.Attachments is { Count: > 0 })
                chatClient.SetAttachments(request.Attachments);

            // 3. await streaming — update response node via the stream
            var responseText = new StringBuilder();
            var lastUpdate = DateTimeOffset.MinValue;
            var lastStatusUpdate = DateTimeOffset.MinValue;
            string? currentStatus = null;
            var toolCallLog = new List<ToolCallEntry>();
            var pendingCalls = new Dictionary<string, FunctionCallContent>();
            string? firstDelegationPath = null;
            string? lastCallKey = null;

            // Set execution context for delegation sub-thread creation
            // Capture the user's AccessContext from the delivery so it propagates through delegations
            var userAccessContext = delivery.AccessContext;
            logger.LogInformation("[ThreadExec] ExecuteMessageAsync: user={User}, threadPath={ThreadPath}",
                userAccessContext?.ObjectId ?? "(no-user)", threadPath);
            chatClient.SetExecutionContext(new ThreadExecutionContext
            {
                ThreadPath = threadPath,
                ResponseMessageId = responseMsgId,
                UserAccessContext = userAccessContext
            });
            chatClient.UpdateDelegationStatus = status => { currentStatus = status; };
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

                // Update execution status when it changes (throttled)
                if (currentStatus != null && responseText.Length == 0
                    && DateTimeOffset.UtcNow - lastStatusUpdate > TimeSpan.FromMilliseconds(300))
                {
                    var status = currentStatus;
                    var liveToolCalls = toolCallLog.ToImmutableList();
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
                        ExecutionStatus = null
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
                        ExecutionStatus = null
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
        var execHub = hub.GetHostedHub(new Address($"{hub.Address}/_Exec"), HostedHubCreation.Never);
        execHub?.CancelCurrentExecution();
        return delivery.Processed();
    }
}
