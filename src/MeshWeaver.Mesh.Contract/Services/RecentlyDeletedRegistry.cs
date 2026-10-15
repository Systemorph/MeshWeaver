using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Mesh-scoped durable tombstone for just-deleted node paths — the "delete wins" guard against the
/// resurrect-after-delete write race.
///
/// <para>When a node is deleted, a per-node hub that (re)activates AFTER the delete gets a STALE
/// own-node snapshot from the routing catalog stream's <c>Replay(1)</c> buffer. Its per-node
/// TypeSource workspace then sees that snapshot as an "add" and queues a debounced save that
/// RE-PERSISTS (resurrects) the deleted row — the intermittent <c>SpaceDeletionPartitionDropTests</c>
/// flake (a save lands on the deleted path ~200 ms after the delete, so every later read correctly
/// sees a live row).</para>
///
/// <para>Population is <b>synchronous, at the delete source</b>: <c>HandleDeleteNodeRequest</c> marks
/// every path it deletes here BEFORE it returns its response — so the tombstone is in place before the
/// deleting call completes, and therefore before any later hub can activate and resurrect the row.
/// This is what makes the guard deterministic: an earlier attempt that populated only from the async
/// per-hub <c>storage.Changes</c> subscriber still raced at cold start (no per-node hub was active yet
/// to observe the delete). Per-node hubs (via the Graph MeshDataSource guards) then READ this registry
/// and drop a resurrecting save; a legitimate re-create <see cref="Supersede"/>s the tombstone.</para>
///
/// <para><b>Superseding is SYNCHRONOUS at the durable write, exactly as marking is synchronous at the
/// delete (#3008).</b> The storage write seam (the outermost <c>IStorageAdapter</c> decorator) calls
/// <see cref="Supersede"/> on the write's post-commit emission — strictly BEFORE the caller's
/// <c>IMeshChangeFeed</c> <c>Created</c> publish, which is composed downstream of that emission. Until
/// this existed the only clear ran inside a LIVE per-node hub's change handler, i.e. asynchronously,
/// incidentally (it needed a hub to be alive for the path at that moment) and conditionally (a
/// same-version recreate was skipped as a self-write echo) — so a reader whose delivery landed on the
/// still-disposing old hub was NACKed with the authoritative "the node was deleted, so this address
/// will not reactivate" for a node that provably existed again, and the reader stopped, by contract.</para>
///
/// <para>A superseded tombstone answers <see cref="IsRecentlyDeleted"/> / <see cref="IsDeleted"/> with
/// <c>false</c> — the address is not gone for good and a save to it is not a resurrection — but the
/// delete stays ON RECORD for the TTL, together with the version the recreate landed at: that is what
/// lets <c>MeshNodeTypeSource</c> tell a legitimate version rewind (a recreate restarting at
/// <c>Version = 1</c>) from a stale replay (<see cref="IsRecreatedAt"/>). Erasing the entry at the write
/// would have thrown that recognition away for a hub whose own-node stream delivers the recreate later.</para>
///
/// <para>Instance-only state (a <see cref="ConcurrentDictionary{TKey,TValue}"/> — the lifetime is the
/// mesh singleton's, never <c>static</c>; see NoStaticState.md). TTL-bounded so the map can't grow
/// unbounded; superseded entries expire on the same TTL.</para>
///
/// <para>It is also the mesh's <see cref="IAddressTombstones"/>: the message pipeline reads it to
/// tell "this hub is going down because its node was DELETED" (the address is gone for good) from
/// "this hub is recycling / restarting" (it will reactivate), which decides whether an abandoned
/// delivery is NACKed as an authoritative NotFound or as the transient
/// <c>ErrorType.ShuttingDown</c>. See <see cref="IAddressTombstones"/> for why that distinction is
/// load-bearing.</para>
/// </summary>
public sealed class RecentlyDeletedRegistry : IAddressTombstones
{
    // Long enough to cover the full delete → (deactivate) → re-activate → debounced-flush window
    // under CI load (the resurrecting save fires ~200 ms after the delete; the guard must outlive
    // the slowest activation), short enough that a stale entry only ever blocks one save that would
    // itself be a no-op. A re-create clears the entry explicitly, so the TTL is only a backstop.
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// One delete on record. <see cref="SupersededAtVersion"/> is <c>null</c> while the tombstone is
    /// LIVE (the address is gone for good); once a durable write lands on the path it carries the
    /// version that write committed at — the LOWEST such version, i.e. the recreate itself.
    /// </summary>
    private readonly record struct Tombstone(DateTimeOffset DeletedAt, long? SupersededAtVersion);

    private readonly ConcurrentDictionary<string, Tombstone> _deleted =
        new(StringComparer.OrdinalIgnoreCase);

    // UtcTicks of the last opportunistic prune sweep — gates the O(n) sweep in MarkDeleted to at
    // most once per TTL so a delete burst stays amortised O(1) while the map stays TTL-bounded even
    // for tombstones that are never re-checked (IsRecentlyDeleted only prunes the key it looks up).
    private long _lastPruneTicks;

    /// <summary>Records <paramref name="path"/> as just-deleted. Called synchronously from the delete
    /// handler for every deleted path. Opportunistically prunes expired tombstones (time-gated) so a
    /// delete that is never re-read afterwards doesn't leak an entry forever.</summary>
    public void MarkDeleted(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        var now = DateTimeOffset.UtcNow;
        _deleted[path] = new Tombstone(now, SupersededAtVersion: null);

        // Time-gated sweep: only one thread prunes per TTL window (CAS on _lastPruneTicks), so the
        // map can't accumulate tombstones for one-off deletes that are never checked/cleared again.
        var last = Interlocked.Read(ref _lastPruneTicks);
        if (now.UtcTicks - last > Ttl.Ticks
            && Interlocked.CompareExchange(ref _lastPruneTicks, now.UtcTicks, last) == last)
        {
            foreach (var kv in _deleted)
                if (now - kv.Value.DeletedAt > Ttl)
                    _deleted.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>Removes the tombstone for <paramref name="path"/> outright. Prefer
    /// <see cref="Supersede"/> for a (re)create — it keeps the delete on record so a version rewind
    /// is still recognised as the recreate; <c>Clear</c> forgets that too.</summary>
    public void Clear(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            _deleted.TryRemove(path, out _);
    }

    /// <summary>
    /// Records that a durable write landed on <paramref name="path"/> at <paramref name="version"/>
    /// AFTER its delete — the tombstone is superseded: the address is no longer "gone for good"
    /// (<see cref="IsDeleted"/> / <see cref="IsRecentlyDeleted"/> turn <c>false</c>) while the delete
    /// stays on record for the TTL so <see cref="IsRecreatedAt"/> can recognise the recreate.
    ///
    /// <para>Called synchronously by the storage write seam on the write's post-commit emission, and
    /// by a live per-node hub's change handler when it adopts a persisted (re)create. Untracked paths
    /// (the overwhelming majority of writes) cost one dictionary probe and nothing else; a tombstone
    /// already superseded keeps the LOWER version — the first write after the delete IS the recreate,
    /// every later one is an ordinary update above it.</para>
    /// </summary>
    public void Supersede(string? path, long version)
    {
        if (string.IsNullOrEmpty(path) || _deleted.IsEmpty)
            return;
        var now = DateTimeOffset.UtcNow;
        while (_deleted.TryGetValue(path, out var current))
        {
            // An expired tombstone is already dead for every reader (TryGetLive prunes it on
            // access) — prune it here too rather than stamp a recreate version onto a corpse.
            if (now - current.DeletedAt > Ttl)
            {
                _deleted.TryRemove(new KeyValuePair<string, Tombstone>(path, current));
                return;
            }
            if (current.SupersededAtVersion is { } already && already <= version)
                return;
            if (_deleted.TryUpdate(path, current with { SupersededAtVersion = version }, current))
                return;
        }
    }

    /// <summary>True when <paramref name="path"/> was deleted within the TTL window and has not
    /// been re-created since — the caller (a per-node hub's save path) must then DROP the write.
    /// Expired entries are pruned on access; a superseded entry answers <c>false</c>.</summary>
    public bool IsRecentlyDeleted(string? path)
        => TryGetLive(path, out var tombstone) && tombstone.SupersededAtVersion is null;

    /// <summary>
    /// True when <paramref name="path"/> was deleted within the TTL window and a durable write has
    /// since landed on it at or below <paramref name="version"/> — i.e. an own-node emission carrying
    /// <paramref name="version"/> IS the recreate (or a later update of it), not a stale replay of the
    /// pre-delete node. The version floor in <c>MeshNodeTypeSource</c> resets on this instead of
    /// dropping the emission as a regression.
    /// </summary>
    public bool IsRecreatedAt(string? path, long version)
        => TryGetLive(path, out var tombstone)
           && tombstone.SupersededAtVersion is { } recreatedAt
           && version >= recreatedAt;

    private bool TryGetLive(string? path, out Tombstone tombstone)
    {
        tombstone = default;
        if (string.IsNullOrEmpty(path) || !_deleted.TryGetValue(path, out tombstone))
            return false;
        if (DateTimeOffset.UtcNow - tombstone.DeletedAt > Ttl)
        {
            _deleted.TryRemove(path, out _);
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The pipeline-facing name for <see cref="IsRecentlyDeleted"/>: same tombstone, same TTL,
    /// same "a re-create supersedes it" semantics. Kept as a separate member so the message pipeline
    /// depends on the intent (<see cref="IAddressTombstones"/> — "is this address gone for good?")
    /// rather than on this class, which lives above it in the reference graph.
    /// </remarks>
    public bool IsDeleted(string? path) => IsRecentlyDeleted(path);

    // ─── Active subtree deletions ────────────────────────────────────────────
    //
    // Unlike the TTL tombstones above (a backstop against the resurrect-save
    // race), an ACTIVE subtree deletion is a hard invariant with an explicit
    // lifetime: HandleDeleteNodeRequest opens a scope BEFORE it enumerates the
    // deletion plan and closes it when the operation completes (success OR
    // failure — the scope rides an Observable.Using, so error/timeout/unsubscribe
    // all release it). While the scope is open, the storage write guard
    // (SubtreeDeletionGuardStorageAdapter) refuses every write at or under the
    // root — so a node created mid-delete (e.g. a compile-watcher Release
    // satellite) cannot land under a subtree that is being torn down. No timer,
    // no TTL: the invariant holds exactly as long as the delete is in flight.

    private readonly ConcurrentDictionary<string, int> _activeSubtreeDeletions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Marks <paramref name="rootPath"/>'s subtree as being deleted. Ref-counted, so two
    /// concurrent deletes of the same root (double-click) each hold the scope independently.
    /// Dispose the returned scope when the delete operation completes — success or failure.
    /// </summary>
    public IDisposable BeginSubtreeDeletion(string rootPath)
    {
        if (string.IsNullOrEmpty(rootPath))
            return EmptyScope.Instance;
        _activeSubtreeDeletions.AddOrUpdate(rootPath, 1, (_, count) => count + 1);
        return new SubtreeDeletionScope(this, rootPath);
    }

    // Row-only deletions (DeleteNodeRequest.KeepDescendants): the same hard invariant as a
    // subtree deletion, for the ONE path being removed — writes under it are not guarded,
    // because the operation leaves them in place.
    private readonly ConcurrentDictionary<string, int> _activeRowDeletions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Marks the node at <paramref name="path"/> — and only that node, never what lies under it —
    /// as being deleted, for a row-only delete (<c>DeleteNodeRequest.KeepDescendants</c>). While the
    /// scope is open the storage write guard refuses a write AT <paramref name="path"/>, so the
    /// delete's version precondition still holds at its commit; writes under the path proceed.
    /// Ref-counted like <see cref="BeginSubtreeDeletion"/>; dispose when the delete completes.
    /// </summary>
    /// <param name="path">The node being deleted alone.</param>
    public IDisposable BeginRowDeletion(string path)
    {
        if (string.IsNullOrEmpty(path))
            return EmptyScope.Instance;
        _activeRowDeletions.AddOrUpdate(path, 1, (_, count) => count + 1);
        return new SubtreeDeletionScope(this, path, rowOnly: true);
    }

    /// <summary>
    /// Runs <paramref name="body"/> inside a <see cref="BeginSubtreeDeletion"/> scope on
    /// <paramref name="rootPath"/>, and RELEASES the scope BEFORE anything the body produced reaches
    /// the subscriber — its values, its completion and its error alike. Cold: the scope opens on
    /// Subscribe, before the body is subscribed.
    ///
    /// <para>🚨 <b>Why not <c>Observable.Using</c> alone.</b> Rx disposes a <c>Using</c> resource only
    /// once the subscription is torn down, which is AFTER the result has propagated downstream. The
    /// result is the "done" signal, so a caller that acts on it — a follow-up teardown of the same
    /// partition, a create under the path just moved away — runs while the scope is still held, and
    /// is refused as if the operation it was waiting for were still in flight ("a deletion … is
    /// already in flight — one teardown at a time"). <c>HandleDeleteNodeRequest</c> releases its scope
    /// by hand before posting its response for the same reason; this is that ordering as one
    /// primitive. The body's notifications are held until it terminates, the scope is released, and
    /// they are then delivered in order. <c>Using</c> stays underneath for unsubscribe, and the scope's
    /// Dispose is idempotent, so the early release and the safety net are one decrement.</para>
    /// </summary>
    /// <typeparam name="T">The body's element type.</typeparam>
    /// <param name="rootPath">The subtree being deleted (or moved away, or torn down).</param>
    /// <param name="body">The work the scope protects. Subscribed once, inside the scope.</param>
    /// <returns>The body's notifications, delivered after the scope is released.</returns>
    public IObservable<T> WithinSubtreeDeletion<T>(string rootPath, Func<IObservable<T>> body) =>
        Observable.Using(
            () => BeginSubtreeDeletion(rootPath),
            scope => Observable.Defer(body)
                .Materialize()
                .ToList()
                .Do(_ => scope.Dispose())
                .SelectMany(notifications => notifications.ToObservable(ImmediateScheduler.Instance))
                .Dematerialize());

    /// <summary>
    /// True when <paramref name="path"/> equals — or lies under — a subtree root whose
    /// deletion is currently in flight. <paramref name="deletionRoot"/> carries the matching
    /// root for diagnostics. The active-set is empty outside delete operations, so the scan
    /// is O(active deletes), i.e. effectively free on the write hot path.
    /// </summary>
    public bool IsUnderActiveDeletion(string? path, out string? deletionRoot)
    {
        deletionRoot = null;
        if (string.IsNullOrEmpty(path))
            return false;
        if (!_activeRowDeletions.IsEmpty && _activeRowDeletions.ContainsKey(path))
        {
            deletionRoot = path;
            return true;
        }
        if (_activeSubtreeDeletions.IsEmpty)
            return false;
        foreach (var kv in _activeSubtreeDeletions)
        {
            var root = kv.Key;
            if (path.Length < root.Length
                || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            if (path.Length == root.Length || path[root.Length] == '/')
            {
                deletionRoot = root;
                return true;
            }
        }
        return false;
    }

    // ── Version-guarded deletes (#6673) ─────────────────────────────────────────────────────────
    //
    // A delete that names DeleteNodeRequest.ExpectedVersion asks the owning hub to compare that
    // version with the node's LIVE version inside the owner's commit turn. That comparison is only
    // worth something if no OWN write can be acknowledged after it: an own write (UpdateOwn) is
    // acknowledged from the in-memory commit, about one persistence-sampler interval BEFORE it is
    // stored, so a write acked after the comparison would be removed by the delete that the
    // comparison let through. This set is the seal that closes that window. The delete opens it
    // BEFORE it asks the owner to compare, and every own commit turn on the sealed path is refused
    // while it is open — so each own write either committed before the comparison (and the
    // comparison sees its version) or is refused in its turn (and nothing was acknowledged).
    // Exact path only: the precondition is the ROOT's, never a descendant's.

    private readonly ConcurrentDictionary<string, int> _versionGuardedDeletions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Seals <paramref name="path"/> against OWN writes for the duration of a version-guarded delete
    /// (<see cref="DeleteNodeRequest.ExpectedVersion"/>, #6673). Ref-counted like
    /// <see cref="BeginSubtreeDeletion"/>. Dispose the returned scope when the delete completes —
    /// success or failure.
    /// </summary>
    /// <param name="path">The exact path of the node the guarded delete targets.</param>
    /// <returns>The scope; disposing it lifts the seal (idempotent).</returns>
    public IDisposable BeginVersionGuardedDeletion(string path)
    {
        if (string.IsNullOrEmpty(path))
            return EmptyScope.Instance;
        _versionGuardedDeletions.AddOrUpdate(path, 1, (_, count) => count + 1);
        return new CountedScope(_versionGuardedDeletions, path);
    }

    /// <summary>
    /// True while a version-guarded delete of exactly <paramref name="path"/> is in flight. The
    /// owner's own-write commit turn reads this and refuses the write, so no own write is
    /// acknowledged between the guarded delete's version comparison and its commit.
    /// </summary>
    /// <param name="path">The path an own write is about to commit to.</param>
    /// <returns><c>true</c> when the path is sealed.</returns>
    public bool IsVersionGuardedDeletionInFlight(string? path)
        => !string.IsNullOrEmpty(path)
           && !_versionGuardedDeletions.IsEmpty
           && _versionGuardedDeletions.ContainsKey(path);

    private sealed class CountedScope(ConcurrentDictionary<string, int> counts, string key) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            while (true)
            {
                if (!counts.TryGetValue(key, out var count))
                    return;
                if (count <= 1)
                {
                    if (counts.TryRemove(new KeyValuePair<string, int>(key, count)))
                        return;
                }
                else if (counts.TryUpdate(key, count - 1, count))
                    return;
            }
        }
    }

    private void EndSubtreeDeletion(string rootPath, bool rowOnly)
    {
        var active = rowOnly ? _activeRowDeletions : _activeSubtreeDeletions;
        while (true)
        {
            if (!active.TryGetValue(rootPath, out var count))
                return;
            if (count <= 1)
            {
                if (active.TryRemove(
                        new KeyValuePair<string, int>(rootPath, count)))
                    return;
            }
            else if (active.TryUpdate(rootPath, count - 1, count))
                return;
        }
    }

    private sealed class SubtreeDeletionScope(
        RecentlyDeletedRegistry registry, string rootPath, bool rowOnly = false)
        : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                registry.EndSubtreeDeletion(rootPath, rowOnly);
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
