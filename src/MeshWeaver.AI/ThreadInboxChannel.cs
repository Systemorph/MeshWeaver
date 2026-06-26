using System.Collections.Concurrent;
using System.Collections.Immutable;
using MeshWeaver.Messaging;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI;

/// <summary>
/// Per-thread, in-memory "inbox channel" — the two-stage hand-off that lets a
/// running round consume follow-up user messages WITHOUT writing the thread
/// MeshNode mid-stream.
///
/// <para>The thread node and the response cell are SEPARATE hubs. Streaming
/// hammers the response cell (fine). Writing the THREAD node DURING execution,
/// however, races the round + the submission watcher and wedges/crashes. This
/// channel removes that mid-round node write entirely:</para>
///
/// <list type="number">
///   <item><b>Stage 1 — feed (submission watcher).</b> On each thread-node
///     emission the watcher calls <see cref="OfferFromNode"/> (READ ONLY — no
///     node write). While the round is <see cref="ThreadExecutionStatus.Executing"/>
///     it enqueues each newly-pending message id at most once (tracked by
///     <see cref="channeled"/>). Messages pending at round START are NOT offered —
///     those are drained into the round as real input by
///     <c>CommitRoundAndExecute</c>; only follow-ups that arrive mid-round become
///     inline-deliverable.</item>
///   <item><b>Stage 2 — consume (check_inbox).</b> The <c>check_inbox</c> tool
///     calls <see cref="DrainPending"/>, which dequeues everything offered so far,
///     records the ids as <see cref="consumedIds">consumed</see>, and returns the
///     messages for inline delivery — again, NO node write.</item>
///   <item><b>Round-end reconcile.</b> At the round's terminal write
///     (Completed / Cancelled / Error) the execution path folds
///     <see cref="TakeConsumedIds"/> into the SAME single node update
///     (PendingUserMessages -= consumed; IngestedMessageIds ∪= consumed), then
///     calls <see cref="Reset"/>. Messages that were channeled but NOT consumed,
///     or that arrived after the last <c>check_inbox</c>, stay in
///     PendingUserMessages so the submission watcher dispatches a next round for
///     them — nothing is lost.</item>
/// </list>
///
/// <para>🚨 INSTANCE state, never static. Resolved per thread via
/// <see cref="For"/> and held on the thread hub's property bag
/// (<c>hub.Set</c>/<c>hub.Get</c>) so its lifetime is the hub's — it dies with
/// the hub, bleeds across no test and no user. The backing collections are
/// concurrent (offered on the thread hub action block; drained on the LLM pump
/// thread; reconciled on a terminal continuation) and lock-free.</para>
/// </summary>
internal sealed class ThreadInboxChannel
{
    // FIFO of (id, message) offered by the submission watcher (Stage 1) and
    // consumed by check_inbox (Stage 2). Carries the id so DrainPending can record
    // consumption — the id is the PendingUserMessages dict key, not a field on
    // ThreadMessage. Instance field (not static): lifetime = the thread hub.
    private readonly ConcurrentQueue<(string Id, ThreadMessage Message)> queue = new();

    // Ids ever offered this round. Stage 1 enqueues each pending id at most once,
    // even though the node keeps the id in PendingUserMessages until round-end — so
    // a re-OfferFromNode (the node still shows the id) does NOT re-enqueue an
    // already-delivered message. Cleared by Reset() at round-end.
    private readonly ConcurrentDictionary<string, byte> channeled = new();

    // Ids drained by check_inbox this round, in consumption order. Folded into
    // IngestedMessageIds at the round's terminal node write, then cleared by Reset().
    private readonly ConcurrentQueue<string> consumedIds = new();
    private readonly ConcurrentDictionary<string, byte> consumedSeen = new();

    /// <summary>
    /// Resolve-or-create the per-thread channel from the thread hub's property bag.
    /// The first call is the submission-watcher install at thread-hub init (single
    /// threaded), so the channel exists before any round, before check_inbox, and
    /// before any terminal write — later callers always resolve the same instance.
    /// </summary>
    public static ThreadInboxChannel For(IMessageHub threadHub)
    {
        ArgumentNullException.ThrowIfNull(threadHub);
        var existing = threadHub.Get<ThreadInboxChannel>();
        if (existing is not null) return existing;
        var created = new ThreadInboxChannel();
        threadHub.Set(created);
        // Re-read so a benign init-race (two callers before the first Set landed)
        // converges on whichever instance the hub's ConcurrentDictionary kept.
        return threadHub.Get<ThreadInboxChannel>() ?? created;
    }

    /// <summary>
    /// Stage 1. Buffers newly-pending follow-up messages into the channel — READ
    /// ONLY, never writes the node. No-op unless the round is actively
    /// <see cref="ThreadExecutionStatus.Executing"/> (messages pending at round
    /// start are the round's own input, drained by <c>CommitRoundAndExecute</c>;
    /// only mid-round arrivals are inline-deliverable). Enqueues in submission
    /// order (UserMessageIds), each id at most once.
    /// </summary>
    public void OfferFromNode(MeshThread? thread)
    {
        if (thread is null) return;
        // Only buffer follow-ups that arrive DURING an in-flight round. At round
        // start the pending queue is the round's input (drained into Messages by
        // CommitRoundAndExecute); offering it here would double-deliver it as both
        // a satellite cell and an inline check_inbox message.
        if (thread.Status != ThreadExecutionStatus.Executing) return;
        if (thread.PendingUserMessages.IsEmpty) return;

        // Submission order first (UserMessageIds), so check_inbox delivers in order.
        foreach (var id in thread.UserMessageIds)
            if (thread.PendingUserMessages.TryGetValue(id, out var msg) && channeled.TryAdd(id, 0))
                queue.Enqueue((id, msg));

        // Defensive: any pending id not yet present in UserMessageIds (shouldn't
        // happen, but never leak an entry by ignoring it).
        foreach (var (id, msg) in thread.PendingUserMessages)
            if (channeled.TryAdd(id, 0))
                queue.Enqueue((id, msg));
    }

    /// <summary>
    /// Stage 2. Dequeues everything offered so far, records each id as consumed (in
    /// consumption order), and returns the messages for inline delivery. NO node
    /// write. Returns empty when nothing new has been offered since the last drain.
    /// </summary>
    public ImmutableList<ThreadMessage> DrainPending()
    {
        var builder = ImmutableList.CreateBuilder<ThreadMessage>();
        while (queue.TryDequeue(out var item))
        {
            builder.Add(item.Message);
            if (consumedSeen.TryAdd(item.Id, 0))
                consumedIds.Enqueue(item.Id);
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// The ids drained by <c>check_inbox</c> this round, in consumption order.
    /// Folded into the terminal node write (PendingUserMessages -= these;
    /// IngestedMessageIds ∪= these). Snapshot — does not mutate the channel; call
    /// <see cref="Reset"/> at round-end to clear all per-round state.
    /// </summary>
    public ImmutableList<string> TakeConsumedIds() => consumedIds.ToImmutableList();

    /// <summary>
    /// Clears all per-round state — the pending queue (any not-yet-consumed offers,
    /// which remain in the node's PendingUserMessages and re-offer next round), the
    /// channeled set, and the consumed-id record. Called at the round's terminal
    /// boundary after the consumed ids have been folded into the node write.
    /// </summary>
    public void Reset()
    {
        while (queue.TryDequeue(out _)) { }
        while (consumedIds.TryDequeue(out _)) { }
        channeled.Clear();
        consumedSeen.Clear();
    }
}
