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
    /// Handles ResubmitMessageRequest — truncates the thread after the user message id,
    /// drops it from IngestedMessageIds, optionally updates its text. The server-side
    /// watcher in ThreadSubmission re-dispatches a new round.
    /// Thin shim over <see cref="ThreadSubmission.ApplyResubmit"/> to keep one code path.
    /// </summary>
    internal static IMessageDelivery HandleResubmitMessage(
        IMessageHub hub,
        IMessageDelivery<ResubmitMessageRequest> delivery)
    {
        var request = delivery.Message;
        ThreadSubmission.ApplyResubmit(
            hub,
            request.ThreadPath,
            request.MessageId,
            newUserText: request.UserMessageText,
            agentName: null,
            modelName: null);
        return delivery.Processed();
    }
}
