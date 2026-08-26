using System.Collections.Concurrent;
using System.Reactive.Subjects;
using System.Threading;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// 🚨 Per-path DURABLE-VERSION HIGH-WATER — the record of what is ALREADY in storage, so the
/// per-node persistence sampler does not write it a second time (#1249).
///
/// <para>Two things raise it, and they are the two ways a hub comes to hold state it did not mint:
/// the post-commit flush confirming its own write (<see cref="Claim"/> → <see cref="Record"/>), and
/// a DURABLE READ observing the row (<see cref="RecordObservedDurable"/> — the per-node hub's
/// activation seed; <c>MeshNodeStreamHandle.AdoptPersisted</c> does the same for a storage change
/// notification). Both mean "the row is durable at or above V", which is exactly what the skip
/// predicate below needs.</para>
///
/// <para><b>The defect it closes.</b> One patch-driven own-node change reached durable storage by
/// TWO independent routes:</para>
/// <list type="number">
///   <item><b>The post-commit flush.</b> <c>DataExtensions.ApplyMeshNodePatchInTurn</c> chains
///     <c>IPostCommitFlush.Flush</c> off the reduced stream's post-commit emission and the caller's
///     <c>PatchDataResponse</c> ack off THAT — so the row is durable before <c>stream.Update</c>
///     returns. This is what gives cross-hub writes read-after-write; it is the authoritative
///     route.</item>
///   <item><b>The per-node persistence sampler.</b> <c>MeshDataSource</c>'s own-stream
///     <c>Sample(200 ms)</c> posts a <c>SaveMeshNodeRequest</c> for every own-node change, whose
///     handler writes the sampled node. Indispensable for own writes that never went through a
///     patch — a pure duplicate for one that did.</item>
/// </list>
///
/// <para>The two are never ordered against each other: the flush writes from an emission thread,
/// the sampler through the owner's inbox. Under a sustained write rate the row advances while the
/// sampler's message queues, so its write lands as a strict version REGRESSION.
/// <c>MonotonicWriteGuardStorageAdapter</c> correctly refuses it — and then resolves the conflict
/// by merging, which with no common ancestor keeps the string SUPERSET and the array UNION. A
/// deletion the newer write made is therefore silently RE-ADDED. Resurrection is a deliberate
/// trade-off for a GENUINE conflict; this one was manufactured by the framework against itself, on
/// a strictly sequential writer, and it also devalued the guard's alarm into background noise.</para>
///
/// <para>🚨 <b>CLAIMED versus RECORDED — the ordering half (#1557).</b> Recording only on the
/// write's EMISSION left the suppression <b>timed</b>, not ordered: the sampler's queued
/// <c>SaveMeshNodeRequest</c> can reach its handler while the flush's storage round-trip is still
/// in flight, read a high-water that has not been raised yet, and write. Measured at ~4% on the
/// 2-vCPU CI runner — and every occurrence is a real resurrection of deleted content, not merely a
/// red test. So the flush now <see cref="Claim"/>s the version BEFORE its write and resolves the
/// claim afterwards (<see cref="Record"/> on success, <see cref="Release"/> when nothing was
/// persisted). A claim is <b>provisional</b>, and the sampler must not simply drop its write
/// against one — see <see cref="PendingClaim"/>, which is what keeps the ordering fix from turning
/// a duplicate-write bug into a lost-write one.</para>
///
/// <para><b>Why a mesh-scoped, path-keyed registry.</b> <c>IPostCommitFlush</c> is registered once
/// per mesh (<c>AddMeshCatalog</c>) and routes writes by node path, while the sampler's handler runs
/// on the per-node owner hub — the two do not share a hub-scoped service, so a per-hub stamp on
/// <c>OwnNodeCache</c> is invisible to the flush. Registered at the mesh ROOT (see
/// <see cref="MeshBuilder"/>, alongside <see cref="RecentlyDeletedRegistry"/>) so a hub
/// <c>ServiceProvider</c> lookup falls back to the ONE instance — a hub-level registration would
/// create a second one and each side would consult a registry the other never wrote (the exact trap
/// #839 hit with the deletion registry).</para>
///
/// <para><b>Why a VERSION, and why <c>&lt;=</c> is a safe skip predicate.</b> A reference-identity
/// stamp cannot work here: the sampler's gate chain runs in the SAME synchronous fan-out as the
/// flush and — having subscribed at hub init — runs FIRST, so nothing the flush stamps is visible
/// to it. The mark is therefore read at HANDLER time. And two DISTINCT own-node states can never
/// share a version: <c>MeshNodeTypeSource.UpdateImpl</c> re-stamps any own update arriving at (or
/// below) its previous version with <c>MeshNode.NextVersion</c>. So a sampled state at or below the
/// flushed version is either the very state the flush persisted or an older one — never newer
/// content.</para>
///
/// <para>The mark is dropped on delete (<see cref="Forget"/>) so a same-id recreate at
/// <c>Version = 1</c> is never mistaken for already-persisted state — the same rule
/// <c>MonotonicWriteGuardStorageAdapter.Forget</c> follows.</para>
///
/// <para>Footprint: one <c>path → (long, timestamp)</c> entry per node this process has patched or
/// activated, dropped on delete and TTL-bounded (same shape and the same amortised time-gated sweep
/// as <see cref="RecentlyDeletedRegistry"/>) so a portal that touches many distinct paths over a
/// long uptime cannot accumulate entries forever — the 30 s window is what bounds it, and it is two
/// orders of magnitude above the 200 ms sampler debounce every reader is racing. Instance state on
/// a mesh-scoped singleton — it dies with the mesh, never a static.</para>
/// </summary>
public sealed class PostCommitFlushRegistry
{
    // The mark only has to outlive the window between the flush's write and the sampler's queued
    // SaveMeshNodeRequest reaching its handler — the sampler debounces at 200 ms, so 30 s is two
    // orders of magnitude of headroom, and it matches RecentlyDeletedRegistry's TTL. Expiry is
    // fail-SAFE, never fail-wrong: an expired mark simply lets the sampler write again, and that
    // write carries the SAME version the flush already stored, which the store accepts as an
    // equal-version rewrite.
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// One entry per patched path. <paramref name="Pending"/> is non-null exactly while a
    /// <see cref="Claim"/> is unresolved; it emits true when the flush persisted the version and
    /// false when it did not, then completes.
    /// </summary>
    private readonly record struct Entry(long Version, DateTimeOffset At, AsyncSubject<bool>? Pending);

    private readonly ConcurrentDictionary<string, Entry> highWater =
        new(StringComparer.OrdinalIgnoreCase);

    // UtcTicks of the last opportunistic prune sweep — gates the O(n) sweep to at most once per TTL
    // so a write burst stays amortised O(1) while the map stays bounded even for paths that are
    // never read back (HighWater only prunes the key it looks up).
    private long lastPruneTicks;

    /// <summary>
    /// Claims <paramref name="version"/> of <paramref name="path"/> for the flush route BEFORE its
    /// durable write starts — the ordered half of the suppression (#1557). Resolve it with
    /// <see cref="Record"/> (persisted) or <see cref="Release"/> (did not persist); an unresolved
    /// claim makes the sampler DEFER rather than drop, so no write is lost either way.
    /// </summary>
    public void Claim(string? path, long version)
    {
        if (string.IsNullOrEmpty(path))
            return;
        var pending = new AsyncSubject<bool>();
        var now = DateTimeOffset.UtcNow;
        highWater.AddOrUpdate(path, new Entry(version, now, pending), (_, current) =>
            // A claim never lowers the mark, and never displaces an EARLIER unresolved claim —
            // whoever is already waiting on that one must still be answered.
            current.Pending is not null
                ? current
                : new Entry(Math.Max(current.Version, version), now, pending));
    }

    /// <summary>
    /// The unresolved claim for <paramref name="path"/>, or null when the mark is a confirmed
    /// durable version (or there is no mark at all).
    ///
    /// <para>🚨 The sampler must consult this before dropping a write. Against a CONFIRMED mark the
    /// row is already durable and the sampled state is at or below it, so dropping is right.
    /// Against an unresolved CLAIM, whether the row will be durable is not yet known — dropping
    /// there would lose the write outright if the flush then failed, or if the adapter answered the
    /// null "I do not own this path" sentinel. Waiting on this observable and writing only when it
    /// reports false is what makes the ordering fix safe in both directions.</para>
    /// </summary>
    public IObservable<bool>? PendingClaim(string? path) =>
        !string.IsNullOrEmpty(path) && highWater.TryGetValue(path, out var entry) ? entry.Pending : null;

    /// <summary>
    /// Monotonically (max) records that <paramref name="version"/> of <paramref name="path"/> is
    /// durable — confirming any claim the flush made before its write, and raising the mark when
    /// the store reports a higher durable version than ours.
    /// </summary>
    public void Record(string? path, long version)
    {
        if (string.IsNullOrEmpty(path))
            return;
        var now = DateTimeOffset.UtcNow;
        AsyncSubject<bool>? resolved = null;
        highWater.AddOrUpdate(path, new Entry(version, now, null), (_, current) =>
        {
            resolved = current.Pending;
            return new Entry(Math.Max(current.Version, version), now, null);
        });
        Resolve(resolved, persisted: true);
        PruneExpired(now);
    }

    /// <summary>
    /// Amortised, time-gated sweep of expired entries — at most one O(n) pass per <see cref="Ttl"/>,
    /// so a write (or read) burst stays O(1) per call while the map stays bounded even for paths
    /// nothing ever looks up again (<see cref="HighWater"/> only prunes the key it is asked for).
    ///
    /// <para>🚨 EVERY method that can ADD an entry must call this. It used to live inline in
    /// <see cref="Record"/>, which was sound only while the flush was the sole way in. It is not:
    /// <see cref="RecordObservedDurable"/> adds an entry per node ACTIVATION, and a process that
    /// only mirrors — reading and activating many nodes, writing none — never reaches
    /// <see cref="Record"/> at all. Leaving the sweep on the write path alone would let exactly that
    /// process accumulate one entry per path it ever activated, for its whole uptime.</para>
    /// </summary>
    private void PruneExpired(DateTimeOffset now)
    {
        var last = Interlocked.Read(ref lastPruneTicks);
        if (now.UtcTicks - last <= Ttl.Ticks
            || Interlocked.CompareExchange(ref lastPruneTicks, now.UtcTicks, last) != last)
            return;
        foreach (var kv in highWater)
            // Never prune an entry with an unresolved claim — someone is waiting on it.
            if (kv.Value.Pending is null && now - kv.Value.At > Ttl)
                highWater.TryRemove(kv.Key, out _);
    }

    /// <summary>
    /// Raises the mark from a DURABLE READ — the caller has just read <paramref name="path"/> out
    /// of storage at <paramref name="version"/>, so the row IS durable at or above it. Used by the
    /// per-node hub's activation seed (<c>MeshNodeTypeSource.DurableSeed</c>), so the persistence
    /// sampler cannot write that read straight back (#2008).
    ///
    /// <para>🚨 <b>Why not <see cref="Record"/>.</b> Record is the FLUSH's completion signal: it
    /// clears <c>Pending</c> and answers every waiter "persisted: true". A read is not a completed
    /// write, and a read that happens to land while a flush's claim is unresolved would resolve
    /// that claim on the flush's behalf — telling a deferred <c>SaveMeshNodeRequest</c> to drop
    /// itself for a write that may still fail. That turns this suppression into the lost-write bug
    /// <see cref="PendingClaim"/> exists to prevent. So an observation NEVER touches a claim: while
    /// one is unresolved the flush owns the entry and this call is a no-op (the claim resolves in
    /// its own turn, and the redundant echo it lets through in that narrow window is the
    /// equal-version rewrite the store has always accepted). It also never lowers the mark, and
    /// never leaves a waiter stranded — the two ways a raise here could do harm.</para>
    /// </summary>
    public void RecordObservedDurable(string? path, long version)
    {
        if (string.IsNullOrEmpty(path) || version <= 0)
            return;
        var now = DateTimeOffset.UtcNow;
        highWater.AddOrUpdate(path, new Entry(version, now, null), (_, current) =>
            current.Pending is not null || current.Version >= version
                ? current
                : new Entry(version, now, null));
        // This path ADDS entries — one per node activation — and a mirror-only process never calls
        // Record, so the sweep has to run from here too or nothing ever bounds the map.
        PruneExpired(now);
    }

    /// <summary>
    /// Resolves a <see cref="Claim"/> the flush did not make good on — a failed write, an empty
    /// sequence, or the null try-then-claim sentinel meaning "this adapter does not own this path".
    /// Waiters are told the row was NOT persisted, so the sampler becomes the writer of record.
    /// Only acts while the entry still holds exactly the claimed version, so a later flush that
    /// already recorded a HIGHER durable version is never undone by a stale release.
    /// </summary>
    public void Release(string? path, long version)
    {
        if (string.IsNullOrEmpty(path) || !highWater.TryGetValue(path, out var entry))
            return;
        if (entry.Pending is null || entry.Version != version)
            return;
        if (highWater.TryRemove(new KeyValuePair<string, Entry>(path, entry)))
            Resolve(entry.Pending, persisted: false);
    }

    /// <summary>
    /// The highest version the post-commit flush has made durable for <paramref name="path"/> within
    /// the TTL window, or <c>0</c> when this process has not flushed it recently (in which case
    /// nothing is suppressed). Expired entries are pruned on access — never one with an unresolved
    /// claim.
    /// </summary>
    public long HighWater(string? path)
    {
        if (string.IsNullOrEmpty(path) || !highWater.TryGetValue(path, out var entry))
            return 0L;
        if (entry.Pending is not null || DateTimeOffset.UtcNow - entry.At <= Ttl)
            return entry.Version;
        highWater.TryRemove(path, out _);
        return 0L;
    }

    /// <summary>
    /// Drops the mark for a deleted path, so a same-id recreate — which legitimately restarts at
    /// <c>Version = 1</c> — is never read as "already persisted". An unresolved claim is answered
    /// "not persisted" rather than abandoned: a waiter must never be left hanging on a delete.
    /// </summary>
    public void Forget(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        if (highWater.TryRemove(path, out var entry))
            Resolve(entry.Pending, persisted: false);
    }

    private static void Resolve(AsyncSubject<bool>? pending, bool persisted)
    {
        if (pending is null)
            return;
        pending.OnNext(persisted);
        pending.OnCompleted();
    }
}
