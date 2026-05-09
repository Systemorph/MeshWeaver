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
            Type = ThreadMessageType.ExecutedInput,
            // User cells don't have a streaming lifecycle — Submitted on creation.
            // The "queued vs ingested" indicator is derived at the thread level
            // (UserMessageIds minus IngestedMessageIds) so the UI can render
            // queued user cells with a "waiting in queue" treatment without
            // needing per-cell mutations on dispatch.
            Status = ThreadMessageStatus.Submitted
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

        // Materialize the user cell IMMEDIATELY (Status=Queued). The GUI binds
        // to Thread.Messages and renders one LayoutAreaControl per id; without
        // immediate cell creation, queued messages submitted while a previous
        // round is still running are invisible until DispatchRound runs. We
        // ALSO update Thread.Messages + UserMessageIds + PendingUserMessages
        // in one atomic stream Update on the thread node — DispatchRound will
        // see the cell already exists (CreateNode there is idempotent via
        // Catch) and transition Status=Queued → Submitted on ingest.
        //
        // Subscribe is mandatory on both — these are cold observables and the
        // side effects only run on Subscribe. Without this chain,
        // AppendUserInput is silently a no-op and the chat workflow never
        // dispatches the message (the original "chat doesn't work" symptom).
        var logger = workspace.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.ThreadInput");
        logger?.LogDebug(
            "[AppendUserInput] entry workspace.Hub={Hub} threadPath={ThreadPath} msgId={MsgId} agent={Agent} model={Model}",
            workspace.Hub.Address, threadPath, msgId,
            message.AgentName ?? "(null)", message.ModelName ?? "(null)");

        var meshService = workspace.Hub.ServiceProvider
            .GetService<MeshWeaver.Mesh.Services.IMeshService>();
        if (meshService != null)
        {
            // Resolve mainEntity: contextPath wins, fallback to threadPath.
            var mainEntity = message.ContextPath ?? threadPath;
            var userCell = new MeshNode(msgId, threadPath)
            {
                NodeType = ThreadMessageNodeType.NodeType,
                MainNode = mainEntity,
                Content = message
            };
            meshService.CreateNode(userCell).Subscribe(
                _ => logger?.LogDebug(
                    "[AppendUserInput] user cell created at {Path}", $"{threadPath}/{msgId}"),
                ex => logger?.LogDebug(ex,
                    "[AppendUserInput] user cell CreateNode returned error for {Path} (may already exist)",
                    $"{threadPath}/{msgId}"));
        }

        workspace.GetMeshNodeStream().Update(node =>
        {
            logger?.LogDebug(
                "[AppendUserInput] update lambda invoked for {ThreadPath} (node.Path={NodePath} contentType={ContentType})",
                threadPath, node.Path ?? "(null)",
                node.Content?.GetType().Name ?? "(null)");
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
        }).Subscribe(
            updated => logger?.LogDebug(
                "[AppendUserInput] OnNext for {ThreadPath} msgId={MsgId} — msgs={Msgs} userIds={UserIds} pending={Pending}",
                threadPath, msgId,
                (updated.Content as MeshThread)?.Messages.Count ?? -1,
                (updated.Content as MeshThread)?.UserMessageIds.Count ?? -1,
                (updated.Content as MeshThread)?.PendingUserMessages.Count ?? -1),
            ex => logger?.LogWarning(ex,
                "[AppendUserInput] UpdateMeshNode FAILED for thread {ThreadPath} message {MessageId}",
                threadPath, msgId));

        return msgId;
    }
}
