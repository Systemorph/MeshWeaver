using System.Collections.Immutable;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Graph;
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
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        var userMsgId = Guid.NewGuid().ToString("N")[..8];
        var responseMsgId = Guid.NewGuid().ToString("N")[..8];
        var responsePath = $"{threadPath}/{responseMsgId}";

        // 1) Create both cells concurrently
        var inputTask = meshService.CreateNodeAsync(new MeshNode(userMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = new ThreadMessage
            {
                Id = userMsgId,
                Role = "user",
                Text = request.UserMessageText,
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput
            }
        });

        var outputTask = meshService.CreateNodeAsync(new MeshNode(responseMsgId, threadPath)
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
                ModelName = request.ModelName
            }
        });

        Task.WhenAll(inputTask, outputTask).ContinueWith(t =>
        {
            var logger = hub.ServiceProvider.GetService<ILogger<AgentChatClient>>();
            logger?.LogInformation("HandleSubmitMessage: ContinueWith fired for {ThreadPath}, IsFaulted={IsFaulted}",
                threadPath, t.IsFaulted);

            if (t.IsFaulted)
            {
                var error = t.Exception?.Flatten().InnerExceptions.FirstOrDefault()?.Message
                            ?? "Node creation failed";
                logger?.LogError(t.Exception, "HandleSubmitMessage: node creation failed for {ThreadPath}: {Error}",
                        threadPath, error);
                hub.Post(new SubmitMessageResponse { Success = false, Error = error },
                    o => o.ResponseFor(delivery));
                return;
            }

            // 2) Update Thread.Messages via InvokeAsync (runs on hub's execution queue
            // where the workspace stream is guaranteed to have data)
            hub.InvokeAsync(() =>
            {
                var stream = hub.ServiceProvider.GetRequiredService<IWorkspace>()
                    .GetStream(typeof(MeshNode));
                stream?.UpdateMeshNode(threadPath, node =>
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

                // 4) Response — nodes created, streaming started
                hub.Post(new SubmitMessageResponse { Success = true }, o => o.ResponseFor(delivery));
            });
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

        try
        {
            // 1. Prepare agent
            var chatClient = new AgentChatClient(parentHub.ServiceProvider);
            chatClient.SetThreadId(threadPath);
            await chatClient.InitializeAsync(request.ContextPath, request.ModelName);

            if (!string.IsNullOrEmpty(request.AgentName))
                chatClient.SetSelectedAgent(request.AgentName);

            if (request.Attachments is { Count: > 0 })
                chatClient.SetAttachments(request.Attachments);

            // 2. Get remote stream ONCE for the response node
            var responseStream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(responsePath), new MeshNodeReference());

            // 3. await streaming — update response node via the stream
            var responseText = new StringBuilder();
            var lastUpdate = DateTimeOffset.MinValue;
            var chatMessage = new ChatMessage(ChatRole.User, request.UserMessageText);

            await foreach (var update in chatClient.GetStreamingResponseAsync([chatMessage], ct))
            {
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

            // 4. Final update
            var finalText = responseText.ToString();
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
                        ModelName = request.ModelName
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
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ExecuteMessageAsync: error for {ThreadPath}", threadPath);
        }

        return delivery.Processed();
    }

    private static IMessageDelivery HandleCancelStream(
        IMessageHub hub, IMessageDelivery<CancelThreadStreamRequest> delivery)
    {
        var execHub = hub.GetHostedHub(new Address($"{hub.Address}/_Exec"), HostedHubCreation.Never);
        execHub?.CancelCurrentExecution();
        return delivery.Processed();
    }
}
