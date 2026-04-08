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

        var responseMsgId = request.OutputMessageId ?? Guid.NewGuid().ToString("N")[..8];
        var responsePath = $"{request.ThreadPath}/{responseMsgId}";

        // Update Thread: truncate Messages + append new output ID, set executing.
        string? contextPath = null;
        hub.GetWorkspace().UpdateMeshNode(node =>
        {
            contextPath = node.MainNode != node.Path ? node.MainNode : null;
            var thread = node.Content as MeshThread ?? new MeshThread();
            var msgIndex = thread.Messages.IndexOf(request.MessageId);
            if (msgIndex < 0) return node;

            return node with
            {
                Content = thread with
                {
                    Messages = thread.Messages.Take(msgIndex + 1).ToImmutableList().Add(responseMsgId),
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

        // Output cell is created by the click handler (same as GUI creates cells for submit)

        // Push progress to output cell
        hub.Post(new UpdateThreadMessageContent { Text = "Allocating agent..." },
            o => o.WithTarget(new Address(responsePath)));

        // Start execution directly — no waiting for cell creation
        var executionHub = hub.GetHostedHub(
            new Address($"{hub.Address}/_Exec"),
            config => config.WithHandler<SubmitMessageRequest>(ThreadExecution.ExecuteMessageAsync),
            HostedHubCreation.Always);

        executionHub!.Post(new SubmitMessageRequest
        {
            ThreadPath = request.ThreadPath,
            UserMessageText = request.UserMessageText,
            UserMessageId = request.MessageId,
            ResponseMessageId = responseMsgId,
            ResponsePath = responsePath,
            ContextPath = contextPath
        }, o => delivery.AccessContext != null ? o.WithAccessContext(delivery.AccessContext) : o);

        return delivery.Processed();
    }
}
