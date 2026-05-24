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
/// ThreadInput.AppendUserInput), eliminating the duplicate-dispatch races caused
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
    /// Appends a user message into <see cref="Thread.PendingUserMessages"/> on
    /// <paramref name="threadPath"/>. Returns the generated message id.
    ///
    /// <para><b>Inbox pattern.</b> This call only writes the pending dict +
    /// <see cref="Thread.UserMessageIds"/> on the thread node. It does NOT
    /// materialise a user satellite cell and does NOT add the id to
    /// <see cref="Thread.Messages"/> — both happen later, at ingestion time,
    /// when the inbox drains the queue. See
    /// <c>ThreadSubmissionServer.DispatchRound</c> (round-start ingestion)
    /// and <see cref="InboxTool.CheckInbox"/> (mid-stream ingestion).</para>
    ///
    /// <para><b>GUI binding.</b> While an entry sits in
    /// <see cref="Thread.PendingUserMessages"/> the chat view renders it as a
    /// "queued" / "not yet submitted" cell from the dictionary directly. The
    /// transition to a materialised cell in <see cref="Thread.Messages"/>
    /// (the "submitted" / "picked up by inbox" state) happens when the inbox
    /// drains.</para>
    ///
    /// <para>Pending-not-empty + <c>IsExecuting=false</c> wakes the submission
    /// watcher, which dispatches a new round. While
    /// <c>IsExecuting=true</c>, the running round's <c>check_inbox</c> tool
    /// drains the queue into its current response cell.</para>
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
        var logger = workspace.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.ThreadInput");
        logger?.LogDebug(
            "[AppendUserInput] entry workspace.Hub={Hub} threadPath={ThreadPath} msgId={MsgId} agent={Agent} model={Model}",
            workspace.Hub.Address, threadPath, msgId,
            message.AgentName ?? "(null)", message.ModelName ?? "(null)");

        // Single atomic update on the thread node: add to UserMessageIds (preserves
        // submission order for ingestion) and PendingUserMessages (the queue the
        // inbox drains). NOT Messages — Messages is the materialised list updated
        // by the inbox at ingestion time. Updates Pending* hints for the next
        // round's dispatch context.
        //
        // Writes via the caller's own workspace handle — sender is THIS hub
        // (portal / thread / wherever AppendUserInput is called from), and the
        // AsyncLocal AccessContext stamps the caller's identity. With delta-
        // based PatchDataRequest in MeshNodeStreamHandle.UpdateRemote, concurrent
        // writes from different mirrors no longer clobber each other, so we no
        // longer need to funnel through the mesh-hub cache (which would erase
        // the caller's identity and surface as 'no AccessContext' warnings).
        workspace.GetMeshNodeStream(threadPath).Update(node =>
        {
            logger?.LogDebug(
                "[AppendUserInput] update lambda invoked for {ThreadPath} (node.Path={NodePath} contentType={ContentType})",
                threadPath, node.Path ?? "(null)",
                node.Content?.GetType().Name ?? "(null)");
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
            updated => logger?.LogDebug(
                "[AppendUserInput] OnNext for {ThreadPath} msgId={MsgId} — userIds={UserIds} pending={Pending}",
                threadPath, msgId,
                (updated.Content as MeshThread)?.UserMessageIds.Count ?? -1,
                (updated.Content as MeshThread)?.PendingUserMessages.Count ?? -1),
            ex => logger?.LogWarning(ex,
                "[AppendUserInput] UpdateMeshNode FAILED for thread {ThreadPath} message {MessageId}",
                threadPath, msgId));

        return msgId;
    }
}
