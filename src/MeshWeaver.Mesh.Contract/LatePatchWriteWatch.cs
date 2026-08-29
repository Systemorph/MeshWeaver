using System.Collections.Concurrent;
using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// 🚨 Late owner-response watch for cross-hub MeshNode writes — the mirror-side half of the
/// acked-write-loss fix behind <c>TwoSiloRecycleConvergenceTest</c> (main run 30159928718 /
/// PR-645 run 30160988085).
///
/// <para><b>Why this exists.</b> <c>MeshNodeStreamHandle.UpdateRemote</c> bounds the caller's
/// wait for the owner's <c>PatchDataResponse</c> at <c>UpdateResponseWaitBound</c> (~2 s) and
/// falls back to an optimistic emit — deliberately, so a busy owner never stalls the GUI. But
/// the owner's TERMINAL verdict can legitimately arrive later: the disposal NACK
/// (<see cref="MeshNodeErrorCode.OwnerDisposing"/>) lands only after the owner's phased
/// teardown, and the cold-store-defer NotFound can fire up to ~10 s after a reactivation. With
/// the response subscription killed at 2 s, a late NACK was observed by NOBODY — the caller saw
/// success, the write was gone.</para>
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
public sealed class LatePatchResponseRegistry
{
    /// <summary>
    /// 🚨 How long after the patch post a late owner response is still acted upon. This is
    /// PROTOCOL, not tuning: it must dominate every owner-side terminal path — the disposal
    /// NACK after the owner's phased teardown (hosted-hub drain capped at 5 s), the
    /// cold-store-defer NotFound (up to ~10 s after reactivation), and the owner ack watcher's
    /// own 20 s bound. A response beyond this window is indistinguishable from a re-delivered
    /// stale verdict and is ignored.
    ///
    /// <para>🚨 Since #2661 this is ALSO the caller's outer verdict bound: <c>UpdateRemote</c> no
    /// longer completes a write on the bounded-wait expiry, so this window is how long a caller
    /// waits for the commit verdict before the owner is declared to have breached its own terminal
    /// contract. Widening it therefore widens a real caller-visible wait — it is not free tuning.
    /// </para>
    /// </summary>
    public static readonly TimeSpan LateResponseWatchBound = TimeSpan.FromSeconds(30);

    private sealed record Entry(
        string Path,
        DateTimeOffset ExpiresAt,
        Action<PatchDataResponse> OnLateResponse,
        Action<DeliveryFailure> OnLateFailure);

    // Instance state on a mesh-scoped singleton (registered next to IMeshNodeStreamCache) —
    // dies with the mesh. Keyed by the PatchDataRequest delivery id (= the response's
    // RequestId property).
    private readonly ConcurrentDictionary<string, Entry> entries = new();

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
        var now = DateTimeOffset.UtcNow;
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
    public void Complete(string requestId) => entries.TryRemove(requestId, out _);

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
        if (entry.ExpiresAt < DateTimeOffset.UtcNow)
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
        if (entry.ExpiresAt < DateTimeOffset.UtcNow)
            return false;
        entry.OnLateFailure(failure);
        return true;
    }

    /// <summary>Number of armed watches — test seam.</summary>
    public int ArmedCount => entries.Count;

    /// <summary>
    /// The request ids of the currently armed watches — test seam. A test that must construct a
    /// LATE verdict (rather than race for one) needs the correlation id of the patch that is
    /// actually in flight; it is not otherwise observable from outside the write path.
    /// </summary>
    public IReadOnlyCollection<string> ArmedRequestIds => entries.Keys.ToArray();
}
