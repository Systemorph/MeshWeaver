using System;

namespace MeshWeaver.Data;

/// <summary>
/// Optional per-hub hook the <c>PatchDataRequest</c> handler invokes AFTER the
/// in-memory commit to flush the just-committed entity to durable storage. Chaining
/// the owner's <c>PatchDataResponse</c> ack off this — and therefore a cross-hub
/// <c>stream.Update</c> completion — guarantees read-after-write: a subsequent Query's
/// initial snapshot reflects the write rather than racing the per-node hub's ~200 ms
/// persistence debounce (the read side reads storage, not the per-node hub's memory).
///
/// <para>Registered by the MeshNode persistence layer (<c>StoragePostCommitFlush</c>).
/// Absent on data hubs without durable storage, in which case the patch handler acks
/// immediately on the in-memory commit (unchanged behaviour). Implementations MUST
/// no-op (return a completed observable) for entity types they don't persist.</para>
/// </summary>
public interface IPostCommitFlush
{
    /// <summary>
    /// Flush <paramref name="committed"/> durably. The returned observable emits once
    /// the write has landed (the emitted value is immaterial), or completes immediately
    /// for entity types this hook does not persist.
    /// </summary>
    IObservable<bool> Flush(object committed);

    /// <summary>
    /// Publish a <c>MeshChangeKind.Updated</c> change event for <paramref name="committed"/>
    /// to the <c>IMeshChangeFeed</c> — WITHOUT a durable write. The feed drives the
    /// Workspace's <c>_remoteStreamCache</c> eviction (so a fresh <c>GetRemoteStream</c> after
    /// the update sees the new snapshot, not a cached pre-update one) and refreshes synced-query
    /// providers. For a write path that persists by SOME OTHER route, where calling
    /// <see cref="Flush"/> would double-write but the feed publish must still happen. No-op for
    /// entity types this hook does not own. Synchronous, non-blocking; never chained to the ack.
    ///
    /// <para>🚨 <b>Currently UNCALLED, and deliberately so.</b> The MeshNode cross-hub atomic apply
    /// once persisted off-turn and used this; today it chains <see cref="Flush"/> and acks off the
    /// durable write, which is what gives <c>stream.Update</c> read-after-write. That flush IS the
    /// authoritative route for a patch — the duplicate that had to go was the per-node persistence
    /// sampler's second write (#1249), suppressed at the sampler's save handler against the
    /// <c>PostCommitFlushRegistry</c> high-water. So do NOT "fix" a future double-write by swapping
    /// <see cref="Flush"/> for this: dropping the durable write from the ack path re-opens the
    /// read-after-write race this hook's own summary describes.</para>
    /// </summary>
    void PublishUpdated(object committed);
}
