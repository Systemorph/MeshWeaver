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
}
