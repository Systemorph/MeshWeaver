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
    /// it as a new pending message by patching
    /// <see cref="MeshThread.RequestedResubmit"/>. The thread hub's resubmit
    /// watcher consumes the intent and re-dispatches.
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
        var intent = new ResubmitIntent(userMessageId, newUserText, agentName, modelName, DateTime.UtcNow);
        hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
        {
            var t = node.Content as MeshThread ?? new MeshThread();
            return node with { Content = t with { RequestedResubmit = intent } };
        }).Subscribe(
            _ => { },
            ex =>
            {
                logger?.LogWarning(ex,
                    "ResubmitMessage: patch failed for thread {ThreadPath} message {MessageId}",
                    threadPath, userMessageId);
                onError?.Invoke($"ResubmitMessage failed: {ex.Message}");
            });
    }

    // ═════════════════════════════════════════════════════════════════════
    // Delete from message (truncate Messages list at the given message)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Truncates <see cref="MeshThread.Messages"/> starting at
    /// <paramref name="atMessageId"/> by patching
    /// <see cref="MeshThread.RequestedDeleteFromMessageId"/>. The thread hub's
    /// watcher consumes the request and rewrites Messages /
    /// IngestedMessageIds atomically.
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
            var t = node.Content as MeshThread ?? new MeshThread();
            if (t.RequestedDeleteFromMessageId == atMessageId) return node;
            return node with { Content = t with { RequestedDeleteFromMessageId = atMessageId } };
        }).Subscribe(
            _ => { },
            ex => logger?.LogWarning(ex,
                "DeleteFromMessage: patch failed for thread {ThreadPath} message {MessageId}",
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
    /// Records a failed submission by patching one entry into
    /// <see cref="MeshThread.PendingFailures"/> on the target thread. The
    /// thread hub's failure watcher consumes the entry and materialises an
    /// error cell. Works from any hub (the patch is a dict SetItem,
    /// RFC-7396-merge-safe).
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
        var record = new FailureRecord(userText, errorMessage, errorCellId, DateTime.UtcNow);
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.HubThreadExtensions");
        hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
        {
            var t = node.Content as MeshThread ?? new MeshThread();
            if (t.PendingFailures.ContainsKey(userMessageId)) return node;
            return node with { Content = t with { PendingFailures = t.PendingFailures.SetItem(userMessageId, record) } };
        }).Subscribe(
            _ => { },
            ex => logger?.LogWarning(ex,
                "RecordSubmissionFailure: patch failed for thread {ThreadPath} message {MessageId}",
                threadPath, userMessageId));
    }
}
