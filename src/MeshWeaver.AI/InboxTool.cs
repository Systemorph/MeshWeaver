using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI;

/// <summary>
/// Result of draining the inbox — the user messages newly visible to the
/// agent (text + id + the original <see cref="ThreadMessage"/> envelope so the
/// caller can materialise the satellite cell), and the post-drain thread state.
/// Pure record so the drain logic is trivially unit-testable without any hub plumbing.
/// </summary>
public sealed record InboxDrainResult(
    ImmutableList<string> DrainedTexts,
    ImmutableList<string> DrainedIds,
    ImmutableList<ThreadMessage> DrainedMessages,
    MeshThread UpdatedThread);

/// <summary>
/// The <c>check_inbox</c> AI function — the unified ingestion point. Every
/// transition from <see cref="MeshThread.PendingUserMessages"/> into
/// <see cref="MeshThread.Messages"/> goes through this tool:
/// <list type="bullet">
///   <item>Submit (<see cref="ThreadInput.AppendUserInput"/>) writes to
///     <c>PendingUserMessages</c> only — no satellite cell, no
///     <c>Messages</c> update. The GUI binds to both properties and renders
///     pending entries as "queued" cells.</item>
///   <item><b>Round-start ingestion</b> (server watcher when
///     <c>IsExecuting=false</c>): the watcher's <c>DispatchRound</c> drains
///     pending, materialises the satellite cells, allocates a single response
///     cell for the round, and posts to <c>_Exec</c>.</item>
///   <item><b>Mid-round ingestion</b> (<c>check_inbox</c> tool fired by the
///     agent): same drain happens — pending → Messages, materialise satellite
///     cells, mark <see cref="MeshThread.IngestedMessageIds"/>. The current
///     response cell continues streaming; no new response cell is created.</item>
/// </list>
/// </summary>
public static class InboxTool
{
    public const string ToolName = "check_inbox";

    public const string ToolDescription =
        "Check if the user has sent any new messages while you were working. " +
        "Call this between major steps — after each tool call or before starting a new file edit — " +
        "so the user can steer you mid-task. Returns the message text(s) the user typed " +
        "since you last checked, or '(no new messages)' if the queue is empty. " +
        "Once a message is returned by this tool it is permanently delivered to you " +
        "(it won't be re-delivered on a future call) — fold it into your current response.";

    /// <summary>
    /// Pure: given a thread state, returns the drain result + the next thread
    /// state. <c>DrainedIds</c> is the union of <c>UserMessageIds ∩ PendingUserMessages</c>
    /// (in submission order) plus any orphan pending ids not yet in
    /// <c>UserMessageIds</c>. The updated thread has those ids removed from
    /// <c>PendingUserMessages</c>, appended to <c>Messages</c> (so satellite
    /// cells the caller materialises become rendered chat entries), and added
    /// to <c>IngestedMessageIds</c> (de-duplicated).
    /// </summary>
    public static InboxDrainResult Drain(MeshThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        if (thread.PendingUserMessages.IsEmpty)
        {
            return new InboxDrainResult(
                ImmutableList<string>.Empty,
                ImmutableList<string>.Empty,
                ImmutableList<ThreadMessage>.Empty,
                thread);
        }

        var drainedIdsBuilder = ImmutableList.CreateBuilder<string>();
        var drainedTextsBuilder = ImmutableList.CreateBuilder<string>();
        var drainedMessagesBuilder = ImmutableList.CreateBuilder<ThreadMessage>();
        foreach (var id in thread.UserMessageIds)
        {
            if (thread.PendingUserMessages.TryGetValue(id, out var msg))
            {
                drainedIdsBuilder.Add(id);
                drainedTextsBuilder.Add(msg.Text);
                drainedMessagesBuilder.Add(msg);
            }
        }

        // Catch any pending ids not in UserMessageIds (defensive — shouldn't happen but
        // we don't want to leak entries by ignoring them).
        foreach (var (id, msg) in thread.PendingUserMessages)
        {
            if (!drainedIdsBuilder.Contains(id))
            {
                drainedIdsBuilder.Add(id);
                drainedTextsBuilder.Add(msg.Text);
                drainedMessagesBuilder.Add(msg);
            }
        }

        var drainedIds = drainedIdsBuilder.ToImmutable();
        var pendingAfter = thread.PendingUserMessages;
        foreach (var id in drainedIds)
            pendingAfter = pendingAfter.Remove(id);

        var ingestedAfter = thread.IngestedMessageIds;
        foreach (var id in drainedIds)
            if (!ingestedAfter.Contains(id))
                ingestedAfter = ingestedAfter.Add(id);

        // Append drained ids to Messages in submission order, skipping any already present.
        var messagesAfter = thread.Messages;
        foreach (var id in drainedIds)
            if (!messagesAfter.Contains(id))
                messagesAfter = messagesAfter.Add(id);

        return new InboxDrainResult(
            drainedTextsBuilder.ToImmutable(),
            drainedIds,
            drainedMessagesBuilder.ToImmutable(),
            thread with
            {
                Messages = messagesAfter,
                PendingUserMessages = pendingAfter,
                IngestedMessageIds = ingestedAfter
            });
    }

    /// <summary>
    /// Formats the drain result as the tool-call return value the agent sees.
    /// Empty queue → <c>"(no new messages)"</c> so the agent can rapidly poll
    /// without semantic ambiguity. Single message → just the text.
    /// Multiple messages → numbered list.
    /// </summary>
    public static string FormatToolResult(InboxDrainResult drain)
    {
        if (drain.DrainedTexts.IsEmpty)
            return "(no new messages)";

        if (drain.DrainedTexts.Count == 1)
            return $"User sent a follow-up message:\n\n{drain.DrainedTexts[0]}";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"User sent {drain.DrainedTexts.Count} follow-up messages:");
        sb.AppendLine();
        for (var i = 0; i < drain.DrainedTexts.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {drain.DrainedTexts[i]}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds the AIFunction registered with the agent. Each invocation reads the
    /// current thread state once (no streaming subscription), drains pending
    /// messages atomically, and returns the formatted text.
    /// </summary>
    public static AIFunction CreateCheckInboxTool(IMessageHub threadHub, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(threadHub);

        // MEAI AIFunction surface requires Task<string>; CheckInbox is the IObservable<string>
        // production surface. The .FirstAsync().ToTask() bridge runs only at this boundary.
        return AIFunctionFactory.Create(
            method: () => CheckInbox(threadHub, logger).FirstAsync().ToTask(),
            name: ToolName,
            description: ToolDescription);
    }

    /// <summary>
    /// Reads the current thread MeshNode, computes the drain, materialises the
    /// user satellite cells (one <c>CreateNode</c> per drained id — pending entries
    /// don't have cells yet; the GUI rendered them from the dictionary), commits the
    /// drain atomically to the thread workspace via <c>UpdateMeshNode</c>, and
    /// returns the formatted tool result. Errors flow through <c>.Catch</c> and are
    /// returned as the tool result so the agent sees a non-fatal failure message
    /// instead of throwing.
    /// </summary>
    public static IObservable<string> CheckInbox(IMessageHub threadHub, ILogger? logger) =>
        Observable.Defer(() =>
        {
            var workspace = threadHub.GetWorkspace();
            var threadPath = threadHub.Address.Path;
            // Read the current thread node once. `GetMeshNodeStream()` reads the
            // hub's OWN node (the thread).
            return workspace.GetMeshNodeStream()
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(5))
                .Select(node =>
                {
                    var thread = node?.Content as MeshThread;
                    if (thread == null)
                    {
                        logger?.LogDebug(
                            "[InboxTool] thread node has no MeshThread content for {Path}",
                            threadPath);
                        return "(no new messages)";
                    }

                    var drain = Drain(thread);
                    if (drain.DrainedIds.IsEmpty)
                    {
                        logger?.LogDebug("[InboxTool] inbox empty for {Path}", threadPath);
                        return "(no new messages)";
                    }

                    // Materialise the satellite ThreadMessage cells the agent is
                    // about to "see". AppendUserInput didn't create them at submit
                    // time — the inbox is the unified ingestion point.
                    var meshService = threadHub.ServiceProvider
                        .GetService<Mesh.Services.IMeshService>();
                    if (meshService != null)
                    {
                        for (var i = 0; i < drain.DrainedIds.Count; i++)
                        {
                            var id = drain.DrainedIds[i];
                            var msg = drain.DrainedMessages[i];
                            var mainEntity = msg.ContextPath ?? threadPath;
                            var cell = new MeshNode(id, threadPath)
                            {
                                NodeType = ThreadMessageNodeType.NodeType,
                                MainNode = mainEntity,
                                Content = msg
                            };
                            meshService.CreateNode(cell).Subscribe(
                                _ => logger?.LogDebug(
                                    "[InboxTool] materialised user cell {Path}",
                                    $"{threadPath}/{id}"),
                                ex => logger?.LogDebug(ex,
                                    "[InboxTool] user cell create error for {Path} (may already exist)",
                                    $"{threadPath}/{id}"));
                        }
                    }

                    // Apply the drain via an atomic workspace update. Subscribe is mandatory
                    // (cold observable); errors are logged but don't fail the tool — the
                    // agent already received the messages via the return value, and the
                    // worst case is the watcher dispatches a duplicate round (idempotent).
                    workspace.GetMeshNodeStream()
                        .Update(currentNode =>
                        {
                            var current = currentNode.Content as MeshThread ?? new MeshThread();
                            // Re-compute drain against the latest state in case another
                            // append landed between our read and write — we ALWAYS drain
                            // every pending entry so the agent's turn becomes the system
                            // of record for "already shown to agent".
                            var redrain = Drain(current);
                            return currentNode with { Content = redrain.UpdatedThread };
                        })
                        .Subscribe(
                            _ => logger?.LogInformation(
                                "[InboxTool] drained {Count} pending messages on {Path}",
                                drain.DrainedIds.Count, threadPath),
                            ex => logger?.LogWarning(ex,
                                "[InboxTool] drain UpdateMeshNode failed for {Path}",
                                threadPath));

                    return FormatToolResult(drain);
                });
        })
        .Catch((Exception ex) =>
        {
            logger?.LogWarning(ex, "[InboxTool] CheckInbox failed for {Path}", threadHub.Address.Path);
            return Observable.Return($"(error reading inbox: {ex.Message})");
        });
}
