using System.Collections.Concurrent;

namespace MeshWeaver.Graph;

/// <summary>
/// Mesh-scoped durable tombstone for just-deleted node paths — the "delete wins" guard
/// against the resurrect-after-delete write race.
///
/// <para>When a node is deleted, a per-node hub that (re)activates AFTER the delete gets a
/// STALE own-node snapshot from the routing catalog stream's <c>Replay(1)</c> buffer. Its
/// <see cref="MeshNodeTypeSource"/> workspace then sees that snapshot as an "add" and queues a
/// debounced save that RE-PERSISTS (resurrects) the deleted row — confirmed as the intermittent
/// <c>SpaceDeletionPartitionDropTests</c> bulk flake (a <c>SAVE-WRITE</c> lands on the deleted
/// path ~200 ms after the delete, and every subsequent read then correctly sees a live row).</para>
///
/// <para>The per-hub <c>MeshNodeTypeSource._recentlyDeleted</c> guard only covers the SAME hub
/// instance; it can't stop a resurrection driven by a DIFFERENT hub instance that activated after
/// the delete (which starts with an empty per-hub set). This registry is a single MESH-scoped
/// instance (registered in <c>AddGraph</c> alongside <c>PartitionRegistry</c>), so the delete
/// notification caught by the owning hub is visible to any later-activating hub for the same path,
/// which drops its resurrecting save.</para>
///
/// <para>Instance-only state (a <see cref="ConcurrentDictionary{TKey,TValue}"/> — the lifetime is
/// the mesh singleton's, never <c>static</c>; see NoStaticState.md). TTL-bounded so the map can't
/// grow unbounded, and cleared on a legitimate re-create so a same-id recreate persists normally.</para>
/// </summary>
public sealed class RecentlyDeletedRegistry
{
    // Long enough to cover the full delete → (deactivate) → re-activate → debounced-flush window
    // under CI load (the resurrecting save fires ~200 ms after the delete; the guard must outlive
    // the slowest activation), short enough that a stale entry only ever blocks one save that would
    // itself be a no-op. A re-create clears the entry explicitly, so the TTL is only a backstop.
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _deleted =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records <paramref name="path"/> as just-deleted. Called from the owning hub's
    /// <c>storage.Changes</c> Deleted handler.</summary>
    public void MarkDeleted(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            _deleted[path] = DateTimeOffset.UtcNow;
    }

    /// <summary>Clears the tombstone for <paramref name="path"/> — a legitimate (re)create so a
    /// same-id node persists normally. Called from the Created/Updated change handler.</summary>
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
}
