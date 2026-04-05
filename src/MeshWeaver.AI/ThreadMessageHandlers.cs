using System.Collections.Immutable;
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
/// Handlers for thread message operations: resubmit (re-evaluate) and delete.
/// Factored out from ThreadLayoutAreas for clarity.
/// All handlers are synchronous (no await) and use workspace.UpdateMeshNode
/// in the handler body (grain scheduler) or hub.Post (safe from any thread).
/// </summary>
public static class ThreadMessageHandlers
{
    /// <summary>
    /// Handles DeleteFromMessageRequest — truncates Messages from the given message onwards.
    /// </summary>
    internal static IMessageDelivery HandleDeleteFromMessage(
        IMessageHub hub,
        IMessageDelivery<DeleteFromMessageRequest> delivery)
    {
        var request = delivery.Message;
        hub.GetWorkspace().UpdateMeshNode(node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            var msgIndex = thread.Messages.IndexOf(request.MessageId);
            if (msgIndex < 0) return node;
            return node with
            {
                Content = thread with { Messages = thread.Messages.Take(msgIndex).ToImmutableList() }
            };
        });
        return delivery.Processed();
    }

    /// <summary>
    /// Handles ResubmitMessageRequest — keeps the user message, deletes old response nodes,
    /// creates a new response cell, then starts execution.
    /// </summary>
    internal static IMessageDelivery HandleResubmitMessage(
        IMessageHub hub,
        IMessageDelivery<ResubmitMessageRequest> delivery)
    {
        var request = delivery.Message;
        var logger = hub.ServiceProvider.GetRequiredService<ILogger<AgentChatClient>>();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        var responseMsgId = Guid.NewGuid().ToString("N")[..8];
        var responsePath = $"{request.ThreadPath}/{responseMsgId}";

        // 1) Single UpdateMeshNode: read context, collect cells to delete, truncate, set executing
        string? contextPath = null;
        ImmutableList<string> cellsToDelete = ImmutableList<string>.Empty;
        hub.GetWorkspace().UpdateMeshNode(node =>
        {
            contextPath = node.MainNode != node.Path ? node.MainNode : null;
            var thread = node.Content as MeshThread ?? new MeshThread();
            var msgIndex = thread.Messages.IndexOf(request.MessageId);
            if (msgIndex < 0) return node;

            // All cells after the user message must be deleted
            cellsToDelete = thread.Messages.Skip(msgIndex + 1).ToImmutableList();

            return node with
            {
                Content = thread with
                {
                    Messages = thread.Messages.Take(msgIndex + 1).ToImmutableList(),
                    IsExecuting = true,
                    ActiveMessageId = responseMsgId,
                    ExecutionStatus = null,
                    TokensUsed = 0,
                    ExecutionStartedAt = DateTime.UtcNow,
                    StreamingText = null,
                    StreamingToolCalls = null
                }
            };
        });

        // 2) Delete ALL old cells after the user message
        foreach (var oldId in cellsToDelete)
            hub.Post(new DeleteNodeRequest($"{request.ThreadPath}/{oldId}"),
                o => o.WithTarget(new Address(request.ThreadPath)));

        // 3) Create new output cell
        meshService.CreateNode(new MeshNode(responseMsgId, request.ThreadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            }
        }).Subscribe(
            _ =>
            {
                // 4) Link as new last cell + start execution
                hub.GetWorkspace().UpdateMeshNode(node =>
                {
                    var thread = node.Content as MeshThread ?? new MeshThread();
                    return node with
                    {
                        Content = thread with { Messages = thread.Messages.Add(responseMsgId) }
                    };
                });

                var executionHub = hub.GetHostedHub(
                    new Address($"{hub.Address}/_Exec"),
                    config => config.WithHandler<SubmitMessageRequest>(ThreadExecution.ExecuteMessageAsync),
                    HostedHubCreation.Always);

                executionHub!.Post(new SubmitMessageRequest
                {
                    ThreadPath = request.ThreadPath,
                    UserMessageText = request.UserMessageText,
                    ResponsePath = responsePath,
                    ContextPath = contextPath
                }, o => delivery.AccessContext != null ? o.WithAccessContext(delivery.AccessContext) : o);
            },
            ex => logger.LogError(ex, "HandleResubmitMessage: CreateNode failed for {ThreadPath}", request.ThreadPath));

        return delivery.Processed();
    }
}
