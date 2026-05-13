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
/// agent and the post-drain thread state. Pure record so the drain logic is
/// trivially unit-testable without any hub plumbing.
/// </summary>
public sealed record InboxDrainResult(
    ImmutableList<string> DrainedTexts,
    ImmutableList<string> DrainedIds,
    MeshThread UpdatedThread);

/// <summary>
/// The <c>check_inbox</c> AI function — Claude's "should I see if the user
/// just sent something while I was working?" hook. The tool drains
/// <see cref="MeshThread.PendingUserMessages"/> for the current thread
/// (atomic <c>workspace.UpdateMeshNode</c>), adds the drained ids to
/// <see cref="MeshThread.IngestedMessageIds"/> so the server watcher
/// won't re-dispatch them after the current turn, and returns the messages
/// as the tool result so the agent can fold them into the in-flight reply.
///
/// The user satellite cells were already created by
/// <see cref="ThreadInput.AppendUserInput"/> at submit time (they're visible
/// in the chat as queued cells); this tool just promotes them from "queued"
/// to "ingested + delivered to the agent".
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
    /// state. <c>DrainedIds</c> is the subset of <c>UserMessageIds</c> that
    /// was in <c>PendingUserMessages</c>, in <c>UserMessageIds</c> submission
    /// order. The updated thread has those ids removed from <c>PendingUserMessages</c>
    /// and added to <c>IngestedMessageIds</c> (de-duplicated).
    /// </summary>
    public static InboxDrainResult Drain(MeshThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        if (thread.PendingUserMessages.IsEmpty)
        {
            return new InboxDrainResult(
                ImmutableList<string>.Empty,
                ImmutableList<string>.Empty,
                thread);
        }

        var drainedIdsBuilder = ImmutableList.CreateBuilder<string>();
        var drainedTextsBuilder = ImmutableList.CreateBuilder<string>();
        foreach (var id in thread.UserMessageIds)
        {
            if (thread.PendingUserMessages.TryGetValue(id, out var msg))
            {
                drainedIdsBuilder.Add(id);
                drainedTextsBuilder.Add(msg.Text);
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

        return new InboxDrainResult(
            drainedTextsBuilder.ToImmutable(),
            drainedIds,
            thread with
            {
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
    /// Reads the current thread MeshNode, computes the drain, applies it to the
    /// thread workspace via <c>UpdateMeshNode</c>, and returns the formatted
    /// tool result. Errors flow through .Catch and are returned as the tool result
    /// so the agent sees a non-fatal failure message instead of throwing.
    /// </summary>
    public static IObservable<string> CheckInbox(IMessageHub threadHub, ILogger? logger) =>
        Observable.Defer(() =>
        {
            var workspace = threadHub.GetWorkspace();
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
                            threadHub.Address.Path);
                        return "(no new messages)";
                    }

                    var drain = Drain(thread);
                    if (drain.DrainedIds.IsEmpty)
                    {
                        logger?.LogDebug("[InboxTool] inbox empty for {Path}",
                            threadHub.Address.Path);
                        return "(no new messages)";
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
                                drain.DrainedIds.Count, threadHub.Address.Path),
                            ex => logger?.LogWarning(ex,
                                "[InboxTool] drain UpdateMeshNode failed for {Path}",
                                threadHub.Address.Path));

                    return FormatToolResult(drain);
                });
        })
        .Catch((Exception ex) =>
        {
            logger?.LogWarning(ex, "[InboxTool] CheckInbox failed for {Path}", threadHub.Address.Path);
            return Observable.Return($"(error reading inbox: {ex.Message})");
        });
}
