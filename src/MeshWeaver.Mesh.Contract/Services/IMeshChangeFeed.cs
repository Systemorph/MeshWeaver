namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Change feed for mesh data mutations (create/update/delete).
/// Producers call <see cref="Publish"/> after each write.
/// Consumers call <see cref="Subscribe"/> with optional filter.
/// Logical subscribers run in the publishing process; cache consumers use
/// <see cref="IMeshInvalidationFeed"/> to observe commits from every process.
/// </summary>
public interface IMeshChangeFeed
{
    /// <summary>
    /// Publishes a change event to all subscribers.
    /// Called from persistence layer after each write.
    /// </summary>
    void Publish(MeshChangeEvent change);

    /// <summary>
    /// Subscribes to change events with optional kind filter.
    /// </summary>
    /// <param name="handler">Callback invoked for each matching event.</param>
    /// <param name="filter">If set, only events matching this kind are delivered.</param>
    /// <returns>Disposable subscription.</returns>
    IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null);
}

/// <summary>
/// Process-local invalidation feed for caches and live mirrors whose answer becomes stale when a
/// node commits on any replica. Direct <see cref="IMeshChangeFeed.Publish"/> calls reach this feed
/// too; durable storage backends additionally relay their cross-process notifications here.
/// </summary>
/// <remarks>
/// This is deliberately separate from <see cref="IMeshChangeFeed"/>. Logical event consumers can
/// send mail, run an instance sync or append an outbox entry and therefore must retain the
/// publisher's single logical delivery. Cache invalidation is idempotent and must run once in every
/// process, including replicas that did not perform the write.
/// </remarks>
public interface IMeshInvalidationFeed
{
    /// <summary>
    /// Subscribes a process-local invalidation handler, optionally filtered by change kind.
    /// </summary>
    IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null);
}

/// <summary>
/// A mesh data change event emitted after a node is created, updated, or deleted.
/// </summary>
public record MeshChangeEvent(
    string Namespace,
    string Id,
    string Path,
    MeshChangeKind Kind,
    string? NodeType,
    long Version,
    DateTimeOffset Timestamp
)
{
    /// <summary>Builds a <see cref="MeshChangeKind.Created"/> event for the given node.</summary>
    public static MeshChangeEvent Created(MeshNode node)
        => new(node.Namespace ?? "", node.Id, node.Path, MeshChangeKind.Created,
            node.NodeType, node.Version, DateTimeOffset.UtcNow);

    /// <summary>Builds a <see cref="MeshChangeKind.Updated"/> event for the given node.</summary>
    public static MeshChangeEvent Updated(MeshNode node)
        => new(node.Namespace ?? "", node.Id, node.Path, MeshChangeKind.Updated,
            node.NodeType, node.Version, DateTimeOffset.UtcNow);

    /// <summary>Builds a <see cref="MeshChangeKind.Deleted"/> event from a node path.</summary>
    public static MeshChangeEvent Deleted(string path, string? nodeType = null)
    {
        var segments = path.Split('/');
        var id = segments.Length > 0 ? segments[^1] : path;
        var ns = segments.Length > 1 ? string.Join("/", segments[..^1]) : "";
        return new(ns, id, path, MeshChangeKind.Deleted, nodeType, 0, DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// The kind of data change.
/// </summary>
public enum MeshChangeKind
{
    /// <summary>A node was created.</summary>
    Created,
    /// <summary>A node was updated in place.</summary>
    Updated,
    /// <summary>A node was removed.</summary>
    Deleted
}
