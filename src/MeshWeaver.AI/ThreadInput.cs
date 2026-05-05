using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        // Append the message to PendingUserMessages + UserMessageIds only.
        //
        // We deliberately do NOT add to Thread.Messages here — the GUI renders one
        // LayoutAreaControl per id in Messages, and rendering a control before its
        // satellite ThreadMessage node has been created on the hub triggers
        // "Cannot access a disposed object" + spurious area-stream errors. The
        // server-side submission watcher creates the satellite cell first via
        // IMeshService.CreateNode and only after CreateNode confirms success does
        // it add the id into Messages (in the same atomic update that flips
        // IsExecuting=true alongside the response cell id).
        //
        // Subscribe is mandatory — UpdateMeshNode returns a cold IObservable<MeshNode>
        // and the side effect (dsStream.Update) only runs on Subscribe. Without this
        // chain, AppendUserInput is silently a no-op and the chat workflow never
        // dispatches the message — the original "chat doesn't work in prod" symptom.
        var logger = workspace.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.ThreadInput");
        workspace.GetMeshNodeStream().Update(node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            var userIds = thread.UserMessageIds.Contains(msgId)
                ? thread.UserMessageIds
                : thread.UserMessageIds.Add(msgId);
            var pending = thread.PendingUserMessages.SetItem(msgId, message);
            return node with
            {
                Content = thread with
                {
                    UserMessageIds = userIds,
                    PendingUserMessages = pending,
                    PendingAgentName = message.AgentName ?? thread.PendingAgentName,
                    PendingModelName = message.ModelName ?? thread.PendingModelName,
                    PendingContextPath = message.ContextPath ?? thread.PendingContextPath,
                    PendingAttachments = message.Attachments ?? thread.PendingAttachments
                }
            };
        }).Subscribe(
            _ => { },
            ex => logger?.LogWarning(ex,
                "AppendUserInput: UpdateMeshNode failed for thread {ThreadPath} message {MessageId}",
                threadPath, msgId));

        return msgId;
    }
}
