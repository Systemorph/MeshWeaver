namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Change feed for mesh data mutations (create/update/delete).
/// Producers call <see cref="Publish"/> after each write.
/// Consumers call <see cref="Subscribe"/> with optional filter.
/// In monolith: in-process Subject. In Orleans: BroadcastChannel cross-silo.
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
