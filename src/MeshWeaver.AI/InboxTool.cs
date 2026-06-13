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
    /// Builds the AIFunction registered with the agent. Each invocation reads the
    /// current thread state once (no streaming subscription), drains pending
    /// messages atomically, and returns the formatted text.
    /// </summary>
    public static AIFunction CreateCheckInboxTool(IMessageHub threadHub, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(threadHub);

        // 🚨 check_inbox is "fake async" — it awaits NO real I/O leaf (only hub-observable
        // composition), so it is NOT pooled (the AI IoPool is reserved for the LLM-stream
        // `await foreach`) and must not re-acquire a pool slot. It is invoked from INSIDE the
        // round's streaming loop while that loop is PAUSED on this tool call (no concurrent pushes).
        //
        // Mechanism: a TaskCompletionSource bridges the reactive drain to the Task the AIFunction
        // surface requires — the LLM framework awaits this Task inside its OWN streaming pump (on
        // the pool), never our hub. We do ONE stream.Update that drains PendingUserMessages
        // (→ IngestedMessageIds) in a single go, then fill the TCS from its completion.
        //
        // In-flight (mid-round) messages are delivered Claude-Code-style: the drained text is
        // appended inline to the live response output with a marker denoting user input — no
        // separate satellite cells, no output-cell split (the old A7 path is removed). We do NOT
        // append the ids to MeshThread.Messages: a Message id without a satellite cell is a
        // dangling ref that re-triggers the exact missing-node NotFound storm this work fixes.
        return AIFunctionFactory.Create(
            method: (CancellationToken ct) =>
            {
                var access = threadHub.ServiceProvider.GetService<AccessService>();
                // Capture the caller identity and re-seed it so the drain write carries it past any
                // thread hop — a lost AsyncLocal would RLS-deny the write.
                var captured = access?.Context ?? access?.CircuitContext;
                if (captured is not null) access?.SetContext(captured);

                var workspace = threadHub.GetWorkspace();
                var threadPath = threadHub.Address.Path;
                // 🚨 RunContinuationsAsynchronously is load-bearing: TrySetResult fires from the
                // Update(...).Subscribe onNext, which can run on the thread hub's action-block
                // thread. Without it, the LLM pump's await continuation (the rest of the streaming
                // round up to its next true await) would resume SYNCHRONOUSLY on that hub thread —
                // the scheduler-capture wedge class (see feedback_subscribeon_breaks_order).
                var tcs = new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                // A cancelled round (Stop button → executionCts) must not leave the LLM pump
                // awaiting this tool Task forever — propagate the token.
                var ctRegistration = ct.Register(() => tcs.TrySetCanceled(ct));
                ImmutableList<string> drainedTexts = ImmutableList<string>.Empty;

                // ONE stream.Update: get from pending and mark ingested in one go. Keep Messages
                // unchanged (see header) — visibility comes from the inline output append below.
                workspace.GetMeshNodeStream()
                    .Update(node =>
                    {
                        if (node?.Content is not MeshThread thread)
                        {
                            // 🚨 Known deferral: this read is deliberately NOT ContentAs-tolerant —
                            // the ContentAs migration of this exact spot regressed the cancel-race
                            // message-loss test and needs a deterministic repro first (see memory
                            // project_baddata_contentas_pattern). Until then, log LOUDLY so a
                            // degraded-JsonElement thread node (which silently never drains and
                            // makes check_inbox report "(no new messages)" forever) is visible.
                            if (node?.Content is not null)
                                logger?.LogError(
                                    "[InboxTool] check_inbox SKIPPED drain for {Path} — Content is {ContentType}, not MeshThread (degraded node?)",
                                    threadPath, node.Content.GetType().Name);
                            return node!;
                        }
                        var drain = Drain(thread);
                        if (drain.DrainedIds.IsEmpty) return node;
                        drainedTexts = drain.DrainedTexts;
                        return node with { Content = drain.UpdatedThread with { Messages = thread.Messages } };
                    })
                    .Subscribe(
                        _ =>
                        {
                            ctRegistration.Dispose();
                            if (drainedTexts.IsEmpty)
                            {
                                tcs.TrySetResult("(no new messages)");
                                return;
                            }
                            var steering = FormatInFlight(drainedTexts);
                            // Append inline to the live output (Claude-Code style). Race-free for
                            // TWO load-bearing reasons: (a) the streaming loop is PAUSED on this
                            // tool call, so no concurrent Append; and (b) the Sample(...) snapshot
                            // pipeline pushes MATERIALIZED strings — it never reads this
                            // StringBuilder lazily on the timer thread. If snapshots ever start
                            // reading the accumulator directly, this append becomes a data race.
                            var segment = threadHub.Get<ThreadExecution.ActiveResponseSegment>();
                            segment?.ResponseText?.Append("\n\n" + steering + "\n\n");
                            tcs.TrySetResult(steering);
                        },
                        ex =>
                        {
                            ctRegistration.Dispose();
                            logger?.LogWarning(ex, "[InboxTool] check_inbox drain failed for {Path}", threadPath);
                            tcs.TrySetResult($"(error reading inbox: {ex.Message})");
                        });
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
