using System.Collections.Concurrent;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// 🚨 Late owner-response watch for cross-hub MeshNode writes — the mirror-side half of the
/// acked-write-loss fix behind <c>TwoSiloRecycleConvergenceTest</c> (main run 30159928718 /
/// PR-645 run 30160988085).
///
/// <para><b>Why this exists.</b> <c>MeshNodeStreamHandle.UpdateRemote</c> holds a pending
/// <c>hub.Observe</c> callback for the owner's <c>PatchDataResponse</c> only as long as
/// <c>UpdateResponseWaitBound</c> (~2 s) — any longer and the hub's Quiescing phase counts it as
/// a leaked callback. But the owner's TERMINAL verdict can legitimately arrive later: the
/// disposal NACK (<see cref="MeshNodeErrorCode.OwnerDisposing"/>) lands only after the owner's
/// phased teardown, and the cold-store-defer NotFound can fire up to ~10 s after a reactivation.
/// With the response subscription simply killed at 2 s, a late verdict was observed by NOBODY —
/// the caller saw success (that emit was optimistic), and the write was gone.</para>
///
/// <para>🚨 Since #2661 the caller is NOT completed at that bound — a bound expiring is not a
/// commit — so this registry is not a best-effort afterthought: it is the seam the caller's own
/// terminal now arrives on. A late ack completes the write as a success, a late NACK or
/// <see cref="DeliveryFailure"/> faults it.</para>
///
/// <para><b>Why a registry + hub handler, not a detached 30 s <c>hub.Observe</c>
/// subscription.</b> A pending <c>Observe</c> callback holds a <c>responseSubjects</c> entry,
/// and the hub's Quiescing phase counts every such entry as a leaked callback
/// (<c>QuiescingTimedOut</c> — a hard test-teardown failure). A response whose 2 s caller
/// window already closed has NO pending callback, so <c>HandleCallbacks</c> lets it fall
/// through to the regular typed-handler chain — the cache hub's <c>PatchDataResponse</c>
/// handler consults this registry there. Plain dictionary state: nothing pends on the hub, the
/// quiesce budget is untouched, and the registry dies with the mesh's DI scope (instance
/// singleton — never static).</para>
///
/// <para><b>Exactly-once hand-off.</b> The entry is registered BEFORE the patch is posted and
/// removed when the caller's bounded wait delivers a real terminal. A response racing the 2 s
/// timeout is therefore handled exactly once: either the pending callback consumes it (entry
/// already completed → <see cref="Dispatch"/> misses) or the callback is gone (entry still
/// armed → <see cref="Dispatch"/> fires). Expired entries (past
/// <see cref="LateResponseWatchBound"/>) are treated as silence — silence is NEVER retried: a
/// merely-busy owner still applies the original patch when its queue drains.</para>
///
/// <para>🚨 <b>A NACK is not always a <see cref="PatchDataResponse"/> — issue #2661.</b> The
/// owner's RLS refusal is a <see cref="DeliveryFailure"/><c>{ErrorType.Unauthorized}</c>, posted
/// by <c>AccessControlPipeline</c> AHEAD of the owner's action block, and it carries the same
/// correlation id as the ack would. Until #2661 this watch knew only about
/// <c>PatchDataResponse</c>, so a denial that lost the race against the caller's bounded wait
/// reached NOTHING: <c>MessageHub.HandleCallbacks</c> found no live subject, logged "No subject
/// found for response message" and marked the delivery processed. The caller kept a success for a
/// write the owner had refused. <see cref="DispatchFailure"/> is that missing seam, and it obeys
/// the identical exactly-once rule.</para>
/// </summary>
public sealed class LatePatchResponseRegistry : ILatePatchVerdictSink
{
    /// <summary>
    /// 🚨 How long after the patch post a late owner response is still acted upon. A response
    /// beyond this window is indistinguishable from a re-delivered stale verdict and is ignored.
    ///
    /// <para>🚨 <b>What this window actually dominates, and what it does not (#3197).</b> It was
    /// justified as PROTOCOL — a bound chosen to exceed every owner-side terminal path, enumerated
    /// as the disposal NACK after a teardown whose hosted-hub drain was "capped at 5 s", the
    /// cold-store-defer (~10 s), and the ack watcher's 20 s. <b>That cap no longer exists</b>:
    /// <c>HostedHubsCollection.DisposeHubsReactive</c> deliberately dropped its flat
    /// <c>Timeout(5s)</c> in #1317, and the only backstop left over that phase is the disposal
    /// WATCHDOG — a STALL detector, re-armed on every <c>RunLevel</c> transition anywhere in the
    /// subtree, so a large subtree that keeps making progress never trips it. The drain therefore
    /// has no duration bound, and this window cannot be said to dominate it.</para>
    ///
    /// <para>Two further corrections to the old claim. The owner-side paths were enumerated as
    /// ALTERNATIVES, taking their maximum (20 s); in <c>ApplyMeshNodePatchInTurn</c> they compose
    /// ADDITIVELY — cold-store defer (10 s) → identity-gated echo (20 s) → durable flush (10 s).
    /// And the two clocks differ: the owner's starts at HANDLER ENTRY, the caller's at POST, with
    /// unbounded routing/queue latency between them (measured at 33–49 s during a bake, #2543).</para>
    ///
    /// <para>So the honest statement is the operational one: this is the window in which a verdict
    /// is still useful to a caller, not a number proven to exceed every path that can produce one.
    /// What the code now guarantees instead is that the gap is VISIBLE — the ack watcher stands
    /// aside for the disposal NACK only while <see cref="IsAdmissible"/> says a route is armed, and
    /// a verdict that arrives past this window is REPORTED rather than dropped in silence, so
    /// "nothing was ever produced" and "something arrived too late" stop looking identical.</para>
    ///
    /// <para>🚨 Since #2661 this is ALSO the caller's outer verdict bound: <c>UpdateRemote</c> no
    /// longer completes a write on the bounded-wait expiry, so this window is how long a caller
    /// waits for the commit verdict before the owner is declared to have breached its own terminal
    /// contract. Widening it therefore widens a real caller-visible wait — it is not free tuning.
    /// </para>
    /// </summary>
    public static readonly TimeSpan LateResponseWatchBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 🚨 Slack added to <see cref="LateResponseWatchBound"/> before a caller's write is failed for
    /// SILENCE. The registry stops honouring a verdict at exactly the watch bound; firing the
    /// caller's bound at the same instant would race a verdict that is still admissible. One second
    /// is enough to order the two, and it is not a retry, a backoff, or a knob to widen when
    /// something times out — a write still unanswered here has outlived every owner-side terminal
    /// path, so the answer is a fault, not a longer wait.
    /// </summary>
    public static readonly TimeSpan VerdictBoundGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 🚨 The outer bound on a caller-visible mesh WRITE: the instant <c>UpdateRemote</c> gives up
    /// and reports <c>OwnerUnreachable</c>. Public because a bound nobody can see gets re-authored
    /// as a literal somewhere else, and then the two numbers collide.
    ///
    /// <para>They did collide. The convention for a test wait was a hand-written <c>30 s</c> — the
    /// same number as <see cref="LateResponseWatchBound"/>, and one second BELOW this. So a test
    /// awaiting a write always lost the race to the framework's own diagnosis by design: the
    /// assertion reported "the observable emitted nothing at all" one second before the write would
    /// have said <c>OwnerUnreachable — the owner produced no terminal for this patch</c>. The
    /// failure that carried the explanation was never the one anybody read (#2819).</para>
    ///
    /// <para>🚨 Anything waiting on a write must bound itself STRICTLY ABOVE this, so the
    /// framework's terminal wins and names the cause. <c>TestTimeouts.Convergence</c> derives from
    /// it rather than restating it.</para>
    /// </summary>
    public static TimeSpan WriteVerdictBound => LateResponseWatchBound + VerdictBoundGrace;

    private sealed record Entry(
        string Path,
        DateTimeOffset ExpiresAt,
        Action<PatchDataResponse> OnLateResponse,
        Action<DeliveryFailure> OnLateFailure);

    // Instance state on a mesh-scoped singleton (registered next to IMeshNodeStreamCache) —
    // dies with the mesh. Keyed by the PatchDataRequest delivery id (= the response's
    // RequestId property).
    private readonly ConcurrentDictionary<string, Entry> entries = new();

    private readonly ILogger<LatePatchResponseRegistry>? logger;

    /// <summary>
    /// The clock every expiry decision reads. Injectable so the window can be crossed in a test
    /// WITHOUT waiting it out — a 30 s sleep is not a test, and a test-only "expire everything"
    /// method on production code is a backdoor. <see cref="TimeProvider.System"/> in every real
    /// mesh.
    /// </summary>
    private readonly TimeProvider clock;

    /// <summary>
    /// Creates the registry. Both dependencies are OPTIONAL so a bare fixture — one with no logging
    /// registered — can still construct it; the logger carries only the VERDICT_EXPIRED report,
    /// which must never be the reason a mesh fails to start.
    /// </summary>
    public LatePatchResponseRegistry(
        ILogger<LatePatchResponseRegistry>? logger = null,
        TimeProvider? clock = null)
    {
        this.logger = logger;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Arms the late watch for the patch posted as <paramref name="requestId"/>. Registered
    /// BEFORE the post so no response can slip between the caller's bounded wait dying and the
    /// watch arming. Opportunistically prunes expired entries so a burst of never-answered
    /// writes cannot accumulate (writes are low-rate; the scan is proportional to in-flight
    /// writes only).
    /// </summary>
    /// <param name="requestId">The patch request's delivery id.</param>
    /// <param name="path">The target node path (diagnostics).</param>
    /// <param name="onLateResponse">Invoked with the owner's late <see cref="PatchDataResponse"/>,
    /// at most once.</param>
    /// <param name="onLateFailure">Invoked with the owner's late <see cref="DeliveryFailure"/> —
    /// above all the RLS <c>Unauthorized</c> refusal — at most once. Exactly one of the two
    /// callbacks ever runs for a given entry (#2661).</param>
    public void Register(
        string requestId,
        string path,
        Action<PatchDataResponse> onLateResponse,
        Action<DeliveryFailure> onLateFailure)
    {
        var now = clock.GetUtcNow();
        foreach (var kv in entries)
        {
            if (kv.Value.ExpiresAt < now)
                entries.TryRemove(kv.Key, out _);
        }
        entries[requestId] = new Entry(
            path, now + LateResponseWatchBound, onLateResponse, onLateFailure);
    }

    /// <summary>
    /// Disarms the watch: the caller's bounded response wait delivered a real terminal (ack,
    /// rejection, or delivery failure), so there is nothing late to act on.
    /// </summary>
    /// <param name="requestId">The patch request's delivery id.</param>
    public void Complete(string requestId) => TryComplete(requestId);

    /// <summary>
    /// 🚨 <see cref="Complete"/>, but reporting whether this call is the one that took the entry —
    /// which makes the entry itself the ARBITER of "no verdict has claimed this write yet".
    ///
    /// <para>The caller's outer verdict bound needs exactly that. Reading a settled-flag instead
    /// is racy: <see cref="Dispatch"/> removes the entry and then runs the callback, so between
    /// those two steps a verdict is provably in flight while nothing has been claimed yet, and a
    /// deadline firing in that window would fault a write the owner had in fact answered. Losing
    /// this race means the verdict wins, which is the correct outcome.</para>
    /// </summary>
    /// <param name="requestId">The patch request's delivery id.</param>
    /// <returns>True when an armed entry was removed BY THIS CALL.</returns>
    public bool TryComplete(string requestId) => entries.TryRemove(requestId, out _);

    /// <summary>
    /// Delivers a LATE owner response to its armed watch. No-op (false) when the watch was
    /// already disarmed by the caller's bounded wait, was never armed, or has expired past
    /// <see cref="LateResponseWatchBound"/>.
    /// </summary>
    /// <param name="requestId">The response's <c>RequestId</c> correlation property.</param>
    /// <param name="response">The owner's response.</param>
    /// <returns>True when an armed, unexpired watch consumed the response.</returns>
    public bool Dispatch(string requestId, PatchDataResponse response)
    {
        if (!entries.TryRemove(requestId, out var entry))
            return false;
        if (ReportIfExpired(requestId, entry, "PatchDataResponse"))
            return false;
        entry.OnLateResponse(response);
        return true;
    }

    /// <summary>
    /// 🚨 Delivers a LATE owner <see cref="DeliveryFailure"/> to its armed watch — the #2661 seam.
    /// An RLS denial is a <c>DeliveryFailure{ErrorType.Unauthorized}</c>, not a
    /// <see cref="PatchDataResponse"/>, so before this existed a denial that lost the race against
    /// the caller's bounded wait was observed by nobody and the caller kept a success for a refused
    /// write. Same exactly-once / expiry rules as <see cref="Dispatch"/>.
    /// </summary>
    /// <param name="requestId">The failure's <c>RequestId</c> correlation property.</param>
    /// <param name="failure">The owner-side / pipeline NACK.</param>
    /// <returns>True when an armed, unexpired watch consumed the failure.</returns>
    public bool DispatchFailure(string requestId, DeliveryFailure failure)
    {
        if (!entries.TryRemove(requestId, out var entry))
            return false;
        if (ReportIfExpired(requestId, entry, nameof(DeliveryFailure)))
            return false;
        entry.OnLateFailure(failure);
        return true;
    }

    /// <summary>
    /// 🚨 An EXPIRED verdict is a fact, not silence (#3197). The registry used to remove the entry,
    /// see it was past <see cref="LateResponseWatchBound"/> and return <c>false</c> — the same
    /// answer it gives for a request nobody ever armed. So a failing run showed
    /// <c>VERDICT_TIMEOUT</c> with ZERO late-terminal records, and there was no way to tell "the
    /// owner never produced a verdict" from "the owner produced one and it arrived too late" —
    /// two different investigations, one indistinguishable symptom (measured, #2543).
    ///
    /// <para>The verdict is still NOT delivered: past the window it is indistinguishable from a
    /// re-delivered stale one, and acting on it is the bug this bound exists to prevent. It is
    /// reported, and counted, so the next reader can see which case they are in.</para>
    /// </summary>
    /// <returns>True when the entry had expired — the caller must not deliver it.</returns>
    private bool ReportIfExpired(string requestId, Entry entry, string verdictKind)
    {
        var now = clock.GetUtcNow();
        if (entry.ExpiresAt >= now)
            return false;

        System.Threading.Interlocked.Increment(ref expiredVerdicts);
        logger?.LogWarning(
            "[LateWatch] VERDICT_EXPIRED path={Path} request={RequestId} kind={VerdictKind} "
            + "late_by={LateBy}ms window={Window}s — the owner DID answer; it arrived past the late "
            + "watch window and was not delivered. This is not 'no verdict was produced': the "
            + "caller's OwnerUnreachable for this write is a reporting artefact of the delay, not "
            + "evidence that the owner stayed silent.",
            entry.Path, requestId, verdictKind,
            (long)(now - entry.ExpiresAt).TotalMilliseconds,
            (long)LateResponseWatchBound.TotalSeconds);
        return true;
    }

    /// <summary>Number of verdicts that arrived past the window and were therefore not delivered —
    /// the counterpart of the log line, for tests and for a health surface that wants the rate
    /// rather than the individual lines.</summary>
    public int ExpiredVerdicts => System.Threading.Volatile.Read(ref expiredVerdicts);

    private int expiredVerdicts;

    /// <inheritdoc />
    public bool IsAdmissible(string requestId)
        => entries.TryGetValue(requestId, out var entry)
           && entry.ExpiresAt >= clock.GetUtcNow();

    /// <summary>Number of armed watches — test seam.</summary>
    public int ArmedCount => entries.Count;

    /// <summary>
    /// The request ids of the currently armed watches — test seam. A test that must construct a
    /// LATE verdict (rather than race for one) needs the correlation id of the patch that is
    /// actually in flight; it is not otherwise observable from outside the write path.
    /// </summary>
    public IReadOnlyCollection<string> ArmedRequestIds => entries.Keys.ToArray();
}
