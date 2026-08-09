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
/// and drop a resurrecting save; a legitimate re-create <see cref="Clear"/>s the tombstone.</para>
///
/// <para>Instance-only state (a <see cref="ConcurrentDictionary{TKey,TValue}"/> — the lifetime is the
/// mesh singleton's, never <c>static</c>; see NoStaticState.md). TTL-bounded so the map can't grow
/// unbounded, and cleared on a legitimate re-create so a same-id recreate persists normally.</para>
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

    private readonly ConcurrentDictionary<string, DateTimeOffset> _deleted =
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
        _deleted[path] = now;

        // Time-gated sweep: only one thread prunes per TTL window (CAS on _lastPruneTicks), so the
        // map can't accumulate tombstones for one-off deletes that are never checked/cleared again.
        var last = Interlocked.Read(ref _lastPruneTicks);
        if (now.UtcTicks - last > Ttl.Ticks
            && Interlocked.CompareExchange(ref _lastPruneTicks, now.UtcTicks, last) == last)
        {
            foreach (var kv in _deleted)
                if (now - kv.Value > Ttl)
                    _deleted.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>Clears the tombstone for <paramref name="path"/> — a legitimate (re)create so a
    /// same-id node persists normally. Called from the Created change handler.</summary>
    public void Clear(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            _deleted.TryRemove(path, out _);
    }

    /// <summary>True when <paramref name="path"/> was deleted within the TTL window and has not
    /// been re-created since — the caller (a per-node hub's save path) must then DROP the write.
    /// Expired entries are pruned on access.</summary>
    public bool IsRecentlyDeleted(string? path)
    {
        if (string.IsNullOrEmpty(path) || !_deleted.TryGetValue(path, out var at))
            return false;
        if (DateTimeOffset.UtcNow - at > Ttl)
        {
            _deleted.TryRemove(path, out _);
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The pipeline-facing name for <see cref="IsRecentlyDeleted"/>: same tombstone, same TTL,
    /// same "a re-create clears it" semantics. Kept as a separate member so the message pipeline
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
