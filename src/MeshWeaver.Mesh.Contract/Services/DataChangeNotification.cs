namespace MeshWeaver.Mesh.Services;

/// <summary>
/// One emission on <see cref="IStorageAdapter.Changes"/> — describes a single
/// commit (Created / Updated / Deleted) at <paramref name="Path"/>. Subscribers
/// (synced-query providers, etc.) react to this to re-emit their downstream
/// observables.
///
/// <para>Carries the post-commit <paramref name="Entity"/> when available so
/// subscribers can pattern-match without an extra read; <c>null</c> for
/// <see cref="DataChangeKind.Deleted"/> events from backends that don't
/// retain a tombstone payload.</para>
/// </summary>
public record DataChangeNotification(
    string Path,
    DataChangeKind Kind,
    object? Entity,
    DateTimeOffset Timestamp
)
{
    /// <summary>
    /// Node type carried by the storage backend when it is available without reading the row.
    /// Cross-process feeds may leave this <see langword="null"/> while an older backend payload is
    /// still deployed; consumers must then treat the notification as unclassified or re-read it.
    /// </summary>
    public string? NodeType { get; init; }

    /// <summary>
    /// Durable node version carried by the storage backend, or <see langword="null"/> when the
    /// backend's notification payload predates version metadata.
    /// </summary>
    public long? Version { get; init; }

    /// <summary>Factory for a Create commit notification.</summary>
    public static DataChangeNotification Created(string path, object? entity) =>
        WithEntityMetadata(new(
            NormalizePath(path), DataChangeKind.Created, entity, DateTimeOffset.UtcNow));

    /// <summary>Factory for an Update commit notification.</summary>
    public static DataChangeNotification Updated(string path, object? entity) =>
        WithEntityMetadata(new(
            NormalizePath(path), DataChangeKind.Updated, entity, DateTimeOffset.UtcNow));

    /// <summary>Factory for a Delete commit notification — <paramref name="entity"/> may be null for backends that don't retain a tombstone payload.</summary>
    public static DataChangeNotification Deleted(string path, object? entity = null) =>
        WithEntityMetadata(new(
            NormalizePath(path), DataChangeKind.Deleted, entity, DateTimeOffset.UtcNow));

    private static DataChangeNotification WithEntityMetadata(DataChangeNotification notification)
        => notification.Entity is MeshNode node
            ? notification with { NodeType = node.NodeType, Version = node.Version }
            : notification;

    private static string NormalizePath(string? path) =>
        path?.Trim('/') ?? "";
}

/// <summary>Kind of commit a <see cref="DataChangeNotification"/> describes.</summary>
public enum DataChangeKind
{
    /// <summary>Newly inserted entity.</summary>
    Created,
    /// <summary>Existing entity replaced or patched.</summary>
    Updated,
    /// <summary>Entity removed from storage.</summary>
    Deleted
}
