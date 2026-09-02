using System.Collections.Concurrent;
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

    /// <summary>
    /// True when <paramref name="path"/> equals — or lies under — a subtree root whose
    /// deletion is currently in flight. <paramref name="deletionRoot"/> carries the matching
    /// root for diagnostics. The active-set is empty outside delete operations, so the scan
    /// is O(active deletes), i.e. effectively free on the write hot path.
    /// </summary>
    public bool IsUnderActiveDeletion(string? path, out string? deletionRoot)
    {
        deletionRoot = null;
        if (string.IsNullOrEmpty(path) || _activeSubtreeDeletions.IsEmpty)
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

    private void EndSubtreeDeletion(string rootPath)
    {
        while (true)
        {
            if (!_activeSubtreeDeletions.TryGetValue(rootPath, out var count))
                return;
            if (count <= 1)
            {
                if (_activeSubtreeDeletions.TryRemove(
                        new KeyValuePair<string, int>(rootPath, count)))
                    return;
            }
            else if (_activeSubtreeDeletions.TryUpdate(rootPath, count - 1, count))
                return;
        }
    }

    private sealed class SubtreeDeletionScope(RecentlyDeletedRegistry registry, string rootPath)
        : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                registry.EndSubtreeDeletion(rootPath);
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
