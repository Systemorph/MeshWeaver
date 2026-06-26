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
    /// <summary>The AI-function name the agent calls to poll for new user messages.</summary>
    public const string ToolName = "check_inbox";

    /// <summary>The tool description shown to the model, instructing when and how to call <c>check_inbox</c>.</summary>
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

        // Restore the invariant UserMessageIds ⊇ IngestedMessageIds — a concurrent
        // cross-hub SubmitMessage can drop an id from the UserMessageIds array (RFC 7396
        // array-replace), while the dict-keyed PendingUserMessages this drain reads keeps
        // it. The owner is authoritative for the derived list (see DispatchRound).
        var userIdsAfter = thread.UserMessageIds;
        foreach (var id in ingestedAfter)
            if (!userIdsAfter.Contains(id))
                userIdsAfter = userIdsAfter.Add(id);

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
                UserMessageIds = userIdsAfter,
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
    /// Builds the AIFunction registered with the agent. Each invocation drains the
    /// per-thread <see cref="ThreadInboxChannel"/> (Stage 2) — purely in memory, NO
    /// thread-node write — and returns the formatted text, appending it inline to
    /// the live response so the agent's continuation renders below the user's
    /// interjection in the SAME response cell (Claude-Code style).
    /// </summary>
    public static AIFunction CreateCheckInboxTool(IMessageHub threadHub, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(threadHub);

        // 🚨 check_inbox is "fake async" — it awaits NO real I/O leaf (no hub round-trip,
        // no stream.Update, no pool slot). It is invoked from INSIDE the round's streaming
        // loop while that loop is PAUSED on this tool call (no concurrent pushes).
        //
        // The drain is now a pure in-memory dequeue from the per-thread ThreadInboxChannel
        // (Stage 1 — the submission watcher — already buffered the pending messages into the
        // channel with NO node write). check_inbox does NOT touch the thread node: the
        // consumed ids are folded into PendingUserMessages → IngestedMessageIds at the
        // round's terminal boundary (ThreadExecution), so the node is written only at round
        // boundaries — never mid-stream where it races the round + the submission watcher.
        //
        // In-flight (mid-round) messages are delivered Claude-Code-style: the drained text is
        // appended inline to the live response output with a marker denoting user input — no
        // separate satellite cells, no output-cell split. We do NOT append the ids to
        // MeshThread.Messages: a Message id without a satellite cell is a dangling ref that
        // re-triggers the exact missing-node NotFound storm this work fixes.
        return AIFunctionFactory.Create(
            // [HiddenTool]: check_inbox is internal plumbing — a high-frequency mid-round
            // poll, not a user action. AgentChatClient reads this marker (via the AIFunction's
            // UnderlyingMethod) and suppresses its tool-call chrome + logs so the chat UI
            // doesn't flash "Calling check_inbox…" on every step. See HiddenToolAttribute.
            method: [Attributes.HiddenTool] (CancellationToken ct) =>
            {
                var threadPath = threadHub.Address.Path;
                // 🚨 RunContinuationsAsynchronously kept as the sanctioned "fake-async" bridge
                // shape: the LLM pump awaits this Task inside its OWN streaming pump (on the
                // pool). The drain itself is synchronous + in-memory, so the result is set
                // before the Task is returned — but the flag stays load-bearing if a caller
                // ever resumes the await on a hub-captured scheduler.
                var tcs = new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                // A cancelled round (Stop button → executionCts) must not leave the LLM pump
                // awaiting this tool Task forever — propagate the token.
                var ctRegistration = ct.Register(() => tcs.TrySetCanceled(ct));
                try
                {
                    // Stage 2: consume the channel. No node write, no Subscribe, no pool slot.
                    var drained = ThreadInboxChannel.For(threadHub).DrainPending();
                    if (drained.IsEmpty)
                    {
                        tcs.TrySetResult("(no new messages)");
                    }
                    else
                    {
                        var steering = FormatInFlight(drained.Select(m => m.Text).ToImmutableList());
                        // Append inline to the live output (Claude-Code style). Race-free for
                        // TWO load-bearing reasons: (a) the streaming loop is PAUSED on this
                        // tool call, so no concurrent Append; and (b) the Sample(...) snapshot
                        // pipeline pushes MATERIALIZED strings — it never reads this
                        // StringBuilder lazily on the timer thread. If snapshots ever start
                        // reading the accumulator directly, this append becomes a data race.
                        var segment = threadHub.Get<ThreadExecution.ActiveResponseSegment>();
                        segment?.ResponseText?.Append("\n\n" + steering + "\n\n");
                        tcs.TrySetResult(steering);
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "[InboxTool] check_inbox drain failed for {Path}", threadPath);
                    tcs.TrySetResult($"(error reading inbox: {ex.Message})");
                }
                finally
                {
                    ctRegistration.Dispose();
                }
                return tcs.Task;
            },
            name: ToolName,
            description: ToolDescription);
    }

    /// <summary>
    /// Renders the drained in-flight user message(s) as clean markdown blockquotes with a subtle
    /// 💬 marker, so each reads as a distinct user interjection inline in the agent's output —
    /// the user's own words, nicely formatted, no explanatory boilerplate.
    /// </summary>
    private static string FormatInFlight(ImmutableList<string> texts)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < texts.Count; i++)
        {
            if (i > 0) sb.AppendLine().AppendLine();
            // Blockquote every line of the message so multi-line input stays inside the quote.
            sb.Append("> 💬 ").Append(texts[i].Replace("\n", "\n> "));
        }
        return sb.ToString();
    }


}
