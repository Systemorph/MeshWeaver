using System.Collections.Concurrent;
using System.Collections.Generic;

namespace MeshWeaver.Social;

/// <summary>
/// Tiny in-memory queue that bridges the approval handler (producer) and the
/// scheduled publisher (consumer). Duplicates by <c>PostPath</c> are collapsed so
/// the scheduler doesn't double-publish a post if an approval event is re-fired.
/// In distributed / Orleans deployments this gets replaced by a cross-silo queue
/// backed by the mesh, but for monolith an in-process ConcurrentDictionary is fine.
/// </summary>
public interface IPublishQueue
{
    /// <summary>Adds or replaces the snapshot for <see cref="PublishableSnapshot.PostPath"/>.</summary>
    void Enqueue(PublishableSnapshot snapshot);

    /// <summary>
    /// Takes the snapshots whose <c>ScheduledAt ≤ now</c>. Returned entries are
    /// removed from the queue. Used by the scheduler's tick handler.
    /// </summary>
    IReadOnlyList<PublishableSnapshot> DrainDue(System.DateTimeOffset now);
}

/// <summary>Default monolith implementation.</summary>
public sealed class InMemoryPublishQueue : IPublishQueue
{
    private readonly ConcurrentDictionary<string, PublishableSnapshot> _pending = new();

    /// <summary>Adds or replaces the snapshot keyed by its <c>PostPath</c>.</summary>
    /// <param name="snapshot">The publishable snapshot to enqueue.</param>
    public void Enqueue(PublishableSnapshot snapshot) => _pending[snapshot.PostPath] = snapshot;

    /// <summary>Removes and returns the snapshots whose <c>ScheduledAt</c> is at or before <paramref name="now"/>.</summary>
    /// <param name="now">The cutoff time; snapshots scheduled at or before this are returned.</param>
    /// <returns>The snapshots that are now due for publishing.</returns>
    public IReadOnlyList<PublishableSnapshot> DrainDue(System.DateTimeOffset now)
    {
        var due = new List<PublishableSnapshot>();
        foreach (var kvp in _pending)
        {
            if (kvp.Value.ScheduledAt <= now && _pending.TryRemove(kvp.Key, out var snap))
                due.Add(snap);
        }
        return due;
    }
}
