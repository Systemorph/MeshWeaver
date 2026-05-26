using System.Collections.Immutable;
using System.Reactive.Linq;
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
/// Canonical client-side surface for thread operations. All thread mutations
/// — create, submit, resubmit, delete-from, mark-done, record-failure — go
/// through these <see cref="IMessageHub"/> extensions. The extensions write
/// to the thread node via <c>hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(...)</c>
/// (or <see cref="CreateNodeRequest"/> for new-thread lifecycle); the
/// per-thread submission watcher reacts to the resulting state changes.
///
/// <para><b>Tests, GUI, and agents all call these methods.</b> There is no
/// other entry point — no <c>SubmitMessageRequest</c>, no parameter-bag
/// context records, no direct <c>hub.Post</c> shortcuts. If you find yourself
/// needing one, the answer is to extend this surface (or fold the new
/// operation into the existing thread-node state machine).</para>
///
/// <para>All methods are <c>void</c> / fire-and-forget. Callers observe
/// confirmation by subscribing to the thread node's remote stream (the same
/// stream the UI already binds to). The optional <c>onError</c> / <c>onCreated</c>
/// callbacks exist for one-shot signalling (e.g. the chat view's "navigate
/// to the new thread once it's created") and fire exactly once.</para>
/// </summary>
public static class HubThreadExtensions
{
    // ═════════════════════════════════════════════════════════════════════
    // Create + submit (new thread)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new thread under <paramref name="namespacePath"/> and queues
    /// the first user message on it. The thread node is created via
    /// <see cref="CreateNodeRequest"/> (node-lifecycle, not a mutation) and
    /// pre-seeded with <see cref="MeshThread.PendingUserMessages"/> so the
    /// submission watcher dispatches the first round as soon as the thread
    /// hub activates — no second round-trip.
    /// </summary>
    public static void StartThread(
        this IMessageHub hub,
        string namespacePath,
        string userText,
        string? agentName = null,
        string? modelName = null,
        string? contextPath = null,
        IReadOnlyList<string>? attachments = null,
        string? createdBy = null,
        string? authorName = null,
        Action<MeshNode>? onCreated = null,
        Action<string>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(namespacePath))
        {
            onError?.Invoke("StartThread requires namespacePath.");
            return;
        }

        var threadNode = ThreadNodeType.BuildThreadNode(namespacePath, userText, createdBy);
        var firstMessageId = Guid.NewGuid().ToString("N")[..8];
        var firstMessage = ThreadInput.CreateUserMessage(
            userText,
            createdBy: createdBy,
            authorName: authorName,
            agentName: agentName,
            modelName: modelName,
            contextPath: contextPath,
            attachments: attachments);

        var seededThread = (threadNode.Content as MeshThread ?? new MeshThread()) with
        {
            Messages = ImmutableList.Create(firstMessageId),
            UserMessageIds = ImmutableList.Create(firstMessageId),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                .SetItem(firstMessageId, firstMessage),
            PendingAgentName = agentName,
            PendingModelName = modelName,
            PendingContextPath = contextPath,
            PendingAttachments = attachments
        };
        threadNode = threadNode with { Content = seededThread };

        var delivery = hub.Post(
            new CreateNodeRequest(threadNode),
            o => o.WithTarget(new Address(namespacePath)));

        if (delivery == null)
        {
            onError?.Invoke("Hub.Post returned null");
            return;
        }

        hub.Observe((IMessageDelivery)delivery)
            .Subscribe(
                response =>
                {
                    if (response.Message is not CreateNodeResponse { Success: true } cnr)
                    {
                        var err = (response.Message as CreateNodeResponse)?.Error ?? "unknown";
                        onError?.Invoke($"Thread creation failed: {err}");
                        return;
                    }
                    onCreated?.Invoke(cnr.Node ?? threadNode);
                },
                ex => onError?.Invoke($"Thread creation failed: {ex.Message}"));
    }

    // ═════════════════════════════════════════════════════════════════════
    // Submit (existing thread)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Submits a user message into an existing thread. Writes
    /// <see cref="MeshThread.PendingUserMessages"/> via
    /// <c>hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(...)</c>.
    /// The per-thread submission watcher drains the queue into a new round.
    /// </summary>
    public static void SubmitMessage(
        this IMessageHub hub,
        string threadPath,
        string userText,
        string? agentName = null,
        string? modelName = null,
        string? contextPath = null,
        IReadOnlyList<string>? attachments = null,
        string? createdBy = null,
        string? authorName = null,
        Action<string>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(threadPath))
        {
            onError?.Invoke("SubmitMessage requires threadPath. Use StartThread for new threads.");
            return;
        }

        var userMessage = ThreadInput.CreateUserMessage(
            userText ?? string.Empty,
            createdBy: createdBy,
            authorName: authorName,
            agentName: agentName,
            modelName: modelName,
            contextPath: contextPath,
            attachments: attachments);
        try
        {
            ThreadInput.AppendUserInput(hub.GetWorkspace(), threadPath, userMessage);
        }
        catch (Exception ex)
        {
            var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger("MeshWeaver.AI.HubThreadExtensions");
            logger?.LogWarning(ex, "[SubmitMessage] AppendUserInput threw for {ThreadPath}", threadPath);
            onError?.Invoke($"SubmitMessage failed: {ex.Message}");
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // Resubmit (truncate after a user message and re-queue it)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Truncates the thread after <paramref name="userMessageId"/> and re-queues
    /// it as a new pending user message. Single <c>stream.Update</c> on the
    /// thread node: drops <c>Messages</c> after the resubmit point, removes the
    /// id from <c>IngestedMessageIds</c>, puts the (optionally edited) user
    /// message back into <c>PendingUserMessages</c>, and resets <c>Status</c>
    /// to <c>Idle</c>. The submission watcher then dispatches the next round
    /// naturally. The user cell's text — a separate node — is updated through
    /// the shared <see cref="IMeshNodeStreamCache"/> when <paramref name="newUserText"/>
    /// is supplied.
    /// </summary>
    public static void ResubmitMessage(
        this IMessageHub hub,
        string threadPath,
        string userMessageId,
        string? newUserText = null,
        string? agentName = null,
        string? modelName = null,
        Action<string>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(threadPath) || string.IsNullOrEmpty(userMessageId))
        {
            onError?.Invoke("ResubmitMessage requires threadPath and userMessageId.");
            return;
        }

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.HubThreadExtensions");

        hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
        {
            var t = node.Content as MeshThread;
            if (t is null) return node;

            var idx = t.Messages.IndexOf(userMessageId);
            if (idx < 0) return node; // id not in thread — no-op

            var keep = t.Messages.Take(idx).ToImmutableList();
            var trimmedUserIds = t.UserMessageIds
                .Where(uid => keep.Contains(uid) || uid == userMessageId)
                .ToImmutableList();
            if (!trimmedUserIds.Contains(userMessageId))
                trimmedUserIds = trimmedUserIds.Add(userMessageId);

            var ingested = t.IngestedMessageIds.Remove(userMessageId);
            var replayMessage = new ThreadMessage
            {
                Role = "user",
                Text = newUserText ?? "",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput,
                AgentName = agentName,
                ModelName = modelName
            };
            var pending = t.PendingUserMessages.SetItem(userMessageId, replayMessage);

            return node with
            {
                Content = t with
                {
                    Messages = keep,
                    UserMessageIds = trimmedUserIds,
                    IngestedMessageIds = ingested,
                    PendingUserMessages = pending,
                    Status = ThreadExecutionStatus.Idle,
                    ActiveMessageId = null,
                    ExecutionStartedAt = null,
                    PendingAgentName = agentName ?? t.PendingAgentName,
                    PendingModelName = modelName ?? t.PendingModelName
                }
            };
        }).Subscribe(
            _ => { },
            ex =>
            {
                logger?.LogWarning(ex,
                    "ResubmitMessage: thread Update failed for {ThreadPath} message {MessageId}",
                    threadPath, userMessageId);
                onError?.Invoke($"ResubmitMessage failed: {ex.Message}");
            });

        // Cell-text update — the per-message satellite is a SEPARATE node, so
        // it goes through the shared cache rather than the thread-node Update.
        // Independent of the thread Update above; no ordering dependency. Only
        // runs when the caller supplied new text.
        if (!string.IsNullOrEmpty(newUserText))
        {
            var cellPath = $"{threadPath}/{userMessageId}";
            var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
            cache.Update(cellPath, node =>
            {
                var existing = node.Content as ThreadMessage;
                var nextContent = existing is not null
                    ? existing with { Text = newUserText!, Timestamp = DateTime.UtcNow }
                    : new ThreadMessage
                    {
                        Role = "user",
                        Text = newUserText!,
                        Timestamp = DateTime.UtcNow,
                        Type = ThreadMessageType.ExecutedInput
                    };
                return node with
                {
                    NodeType = node.NodeType ?? ThreadMessageNodeType.NodeType,
                    Content = nextContent
                };
            }).Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "ResubmitMessage: cell-text Update failed for {CellPath}", cellPath));
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // Delete from message (truncate Messages list at the given message)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Truncates <see cref="MeshThread.Messages"/> starting at
    /// <paramref name="atMessageId"/> (exclusive — drops <paramref name="atMessageId"/>
    /// and everything after). Single <c>stream.Update</c> on the thread node;
    /// no watcher indirection.
    /// </summary>
    public static void DeleteFromMessage(
        this IMessageHub hub, string threadPath, string atMessageId)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(threadPath) || string.IsNullOrEmpty(atMessageId))
            return;

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.HubThreadExtensions");
        hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
        {
            var t = node.Content as MeshThread;
            if (t is null) return node;
            var idx = t.Messages.IndexOf(atMessageId);
            if (idx < 0) return node; // id not in thread — no-op
            return node with
            {
                Content = t with { Messages = t.Messages.Take(idx).ToImmutableList() }
            };
        }).Subscribe(
            _ => { },
            ex => logger?.LogWarning(ex,
                "DeleteFromMessage: Update failed for thread {ThreadPath} message {MessageId}",
                threadPath, atMessageId));
    }

    // ═════════════════════════════════════════════════════════════════════
    // Terminal state — Done / Idle
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Marks the thread <see cref="ThreadExecutionStatus.Done"/> (terminal,
    /// hidden from default catalogs) or re-opens it by flipping back to
    /// <see cref="ThreadExecutionStatus.Idle"/>. Refuses to act while a round
    /// is in flight (the CAS check lives in the Update lambda).
    /// </summary>
    public static void MarkThreadDone(this IMessageHub hub, string threadPath, bool done)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(threadPath))
            return;

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.HubThreadExtensions");
        hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
        {
            var t = node.Content as MeshThread ?? new MeshThread();
            if (t.IsExecuting)
            {
                logger?.LogInformation(
                    "MarkThreadDone: ignored — thread {ThreadPath} is executing (Status={Status})",
                    threadPath, t.Status);
                return node;
            }
            var newStatus = done ? ThreadExecutionStatus.Done : ThreadExecutionStatus.Idle;
            if (t.Status == newStatus) return node;
            return node with { Content = t with { Status = newStatus } };
        }).Subscribe(
            _ => { },
            ex => logger?.LogWarning(ex,
                "MarkThreadDone: stream.Update failed for thread {ThreadPath} done={Done}",
                threadPath, done));
    }

    // ═════════════════════════════════════════════════════════════════════
    // Record failure (one-shot pending entry)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Records a failed submission by creating an error satellite cell and
    /// updating the thread node in one chained operation: <c>CreateNode</c>
    /// for the error cell, then a single <c>stream.Update</c> on the thread
    /// node to append both the user-message id and the error-cell id to
    /// <c>Messages</c> + bookkeeping in <c>UserMessageIds</c> /
    /// <c>IngestedMessageIds</c>. No intent indirection, no watcher.
    /// </summary>
    public static void RecordSubmissionFailure(
        this IMessageHub hub,
        string threadPath,
        string userMessageId,
        string userText,
        string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(threadPath))
            return;

        var errorCellId = Guid.NewGuid().ToString("N")[..8];
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.HubThreadExtensions");
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = hub.GetWorkspace();

        var errorCell = new MeshNode(errorCellId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = threadPath,
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = $"**Submission failed:** {errorMessage}",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            }
        };

        meshService.CreateNode(errorCell)
            .SelectMany(_ => workspace.GetMeshNodeStream(threadPath).Update(node =>
            {
                var t = node.Content as MeshThread ?? new MeshThread();
                var msgs = t.Messages;
                if (!msgs.Contains(userMessageId)) msgs = msgs.Add(userMessageId);
                if (!msgs.Contains(errorCellId)) msgs = msgs.Add(errorCellId);
                var userIds = t.UserMessageIds.Contains(userMessageId)
                    ? t.UserMessageIds
                    : t.UserMessageIds.Add(userMessageId);
                var ingested = t.IngestedMessageIds.Contains(userMessageId)
                    ? t.IngestedMessageIds
                    : t.IngestedMessageIds.Add(userMessageId);
                return node with
                {
                    Content = t with
                    {
                        Messages = msgs,
                        UserMessageIds = userIds,
                        IngestedMessageIds = ingested
                    }
                };
            }))
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "RecordSubmissionFailure: CreateNode+Update failed for {ThreadPath} message {MessageId}",
                    threadPath, userMessageId));
    }
}
