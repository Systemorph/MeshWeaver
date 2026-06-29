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
    /// <param name="submitterObjectId">The submitter's <see cref="AccessContext.ObjectId"/>,
    /// captured at the submit boundary (where the live identity is on the AsyncLocal) and
    /// persisted on the message so the round-dispatch watcher can rebuild the submitter's
    /// identity AFTER the AsyncLocal is wiped across the async boundary. See
    /// <see cref="ThreadMessage.SubmitterObjectId"/>.</param>
    /// <param name="submitterName">The submitter's <see cref="AccessContext.Name"/>, captured
    /// alongside <paramref name="submitterObjectId"/>.</param>
    public static ThreadMessage CreateUserMessage(
        string text,
        string? createdBy = null,
        string? authorName = null,
        string? agentName = null,
        string? modelName = null,
        string? contextPath = null,
        IReadOnlyList<string>? attachments = null,
        string? harness = null,
        string? submitterObjectId = null,
        string? submitterName = null) =>
        new()
        {
            Role = "user",
            Text = text,
            AuthorName = authorName,
            CreatedBy = createdBy,
            AgentName = agentName,
            ModelName = modelName,
            Harness = harness,
            ContextPath = contextPath,
            Attachments = attachments,
            SubmitterObjectId = submitterObjectId,
            SubmitterName = submitterName,
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
    /// Pure: applies a user input to a thread's state — adds <paramref name="msgId"/> to
    /// <see cref="Thread.UserMessageIds"/> (preserving submission order), stashes the
    /// message in <see cref="Thread.PendingUserMessages"/> (the queue the inbox drains), and
    /// keeps the thread's <see cref="Thread.Composer"/> authoritative for the round's sticky
    /// selection (agent / model / harness): when the message carries one, it is folded into
    /// the composer. The round reads the selection from <see cref="Thread.Composer"/> (the
    /// single source of truth), NOT from a thread-level <c>Pending*</c> mirror nor from the
    /// per-message copy. Shared by <see cref="AppendUserInput"/> (the <c>SubmitMessage</c>
    /// path, which carries explicit selection params) and <c>HubThreadExtensions.SubmitComposer</c>
    /// (which already built the message FROM the composer) so both paths leave the composer
    /// current.
    /// </summary>
    public static MeshThread ApplyUserInput(MeshThread thread, string msgId, ThreadMessage message)
    {
        var userIds = thread.UserMessageIds.Contains(msgId)
            ? thread.UserMessageIds
            : thread.UserMessageIds.Add(msgId);
        var composer = (thread.Composer ?? new ThreadComposer()) with
        {
            AgentName = message.AgentName ?? thread.Composer?.AgentName,
            ModelName = message.ModelName ?? thread.Composer?.ModelName,
            Harness = message.Harness ?? thread.Composer?.Harness
        };
        return thread with
        {
            UserMessageIds = userIds,
            PendingUserMessages = thread.PendingUserMessages.SetItem(msgId, message),
            Composer = composer
        };
    }

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
    /// and <c>check_inbox</c> (mid-stream ingestion).</para>
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
        // Promoted from Debug → Info so the delegation-test diagnostics show
        // the submit chain (entry → update lambda → OnNext) at default log level.
        logger?.LogInformation(
            "[AppendUserInput] ENTRY workspace.Hub={Hub} threadPath={ThreadPath} msgId={MsgId} agent={Agent} model={Model}",
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
            logger?.LogInformation(
                "[AppendUserInput] UPDATE_LAMBDA invoked for {ThreadPath} (node.Path={NodePath} contentType={ContentType})",
                threadPath, node.Path ?? "(null)",
                node.Content?.GetType().Name ?? "(null)");
            var thread = node.ContentAs<MeshThread>(workspace.Hub.JsonSerializerOptions, logger);
            // Existing node whose content can't be recovered → leave it alone, NEVER clobber.
            if (node.Content is not null && thread is null)
                return node;
            thread ??= new MeshThread();
            return node with { Content = ApplyUserInput(thread, msgId, message) };
        }).Subscribe(
            updated => logger?.LogInformation(
                "[AppendUserInput] ON_NEXT for {ThreadPath} msgId={MsgId} — userIds={UserIds} pending={Pending}",
                threadPath, msgId,
                updated.ContentAs<MeshThread>(workspace.Hub.JsonSerializerOptions)?.UserMessageIds.Count ?? -1,
                updated.ContentAs<MeshThread>(workspace.Hub.JsonSerializerOptions)?.PendingUserMessages.Count ?? -1),
            ex => logger?.LogWarning(ex,
                "[AppendUserInput] UpdateMeshNode FAILED for thread {ThreadPath} message {MessageId}",
                threadPath, msgId));

        return msgId;
    }
}
