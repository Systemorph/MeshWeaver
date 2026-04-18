using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI;

/// <summary>
/// Testable, Blazor-free helpers for appending user input into a thread.
///
/// The whole submission is one atomic <c>workspace.UpdateMeshNode</c> on the
/// thread node — adding the new id to <c>UserMessageIds</c> and stashing the
/// message payload in <see cref="Thread.PendingUserMessages"/>. The server
/// watcher creates the satellite cell and dispatches the next round.
///
/// This replaces the legacy two-message dance (CreateNodeRequest +
/// AppendUserMessageRequest), eliminating the duplicate-dispatch races caused
/// by interleaved fire-and-forget posts.
/// </summary>
public static class ThreadInput
{
    private static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Pure: builds a user <see cref="ThreadMessage"/> record. No I/O.
    /// </summary>
    public static ThreadMessage CreateUserMessage(
        string text,
        string? createdBy = null,
        string? authorName = null,
        string? agentName = null,
        string? modelName = null,
        string? contextPath = null,
        IReadOnlyList<string>? attachments = null) =>
        new()
        {
            Role = "user",
            Text = text,
            AuthorName = authorName,
            CreatedBy = createdBy,
            AgentName = agentName,
            ModelName = modelName,
            ContextPath = contextPath,
            Attachments = attachments,
            Timestamp = DateTime.UtcNow,
            Type = ThreadMessageType.ExecutedInput
        };

    /// <summary>
    /// Atomically appends a user message to <paramref name="threadPath"/> via a
    /// single <c>workspace.UpdateMeshNode</c> on the thread's MeshNode. Returns
    /// the generated message id. The server-side submission watcher creates the
    /// satellite cell from <see cref="Thread.PendingUserMessages"/> and
    /// dispatches the next round.
    /// </summary>
    public static string AppendUserInput(
        IWorkspace workspace,
        string threadPath,
        ThreadMessage message)
    {
        if (string.IsNullOrEmpty(threadPath))
            throw new ArgumentException("threadPath is required", nameof(threadPath));
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(message);

        var msgId = NewId();

        // Update the single MeshNode in this hub's data source. Using the no-address overload
        // (FirstOrDefault) avoids a pre-existing path-vs-id key mismatch in the address-aware
        // overload. This call expects to run on the thread's own hub (e.g., from the
        // AppendUserMessageRequest handler) where there's exactly one node in the collection.
        workspace.UpdateMeshNode(node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            var msgs = thread.Messages.Contains(msgId)
                ? thread.Messages
                : thread.Messages.Add(msgId);
            var userIds = thread.UserMessageIds.Contains(msgId)
                ? thread.UserMessageIds
                : thread.UserMessageIds.Add(msgId);
            var pending = thread.PendingUserMessages.SetItem(msgId, message);
            return node with
            {
                Content = thread with
                {
                    Messages = msgs,
                    UserMessageIds = userIds,
                    PendingUserMessages = pending,
                    PendingAgentName = message.AgentName ?? thread.PendingAgentName,
                    PendingModelName = message.ModelName ?? thread.PendingModelName,
                    PendingContextPath = message.ContextPath ?? thread.PendingContextPath,
                    PendingAttachments = message.Attachments ?? thread.PendingAttachments
                }
            };
        });

        return msgId;
    }
}
