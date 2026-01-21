using MeshWeaver.Mesh.Query;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Central event bus for data change notifications.
/// Storage adapters publish changes to this notifier, and observable queries subscribe to receive updates.
/// </summary>
public interface IDataChangeNotifier : IObservable<DataChangeNotification>
{
    /// <summary>
    /// Publishes a change notification to all subscribers.
    /// </summary>
    /// <param name="notification">The change notification to publish.</param>
    void NotifyChange(DataChangeNotification notification);
}

/// <summary>
/// Represents a change notification for data at a specific path.
/// </summary>
/// <param name="Path">The normalized path where the change occurred.</param>
/// <param name="Kind">The type of change that occurred.</param>
/// <param name="Entity">The entity that changed (may be null for deletions).</param>
/// <param name="Timestamp">When the change occurred.</param>
public record DataChangeNotification(
    string Path,
    DataChangeKind Kind,
    object? Entity,
    DateTimeOffset Timestamp
)
{
    /// <summary>
    /// Creates a notification for a created entity.
    /// </summary>
    public static DataChangeNotification Created(string path, object entity) =>
        new(NormalizePath(path), DataChangeKind.Created, entity, DateTimeOffset.UtcNow);

    /// <summary>
    /// Creates a notification for an updated entity.
    /// </summary>
    public static DataChangeNotification Updated(string path, object entity) =>
        new(NormalizePath(path), DataChangeKind.Updated, entity, DateTimeOffset.UtcNow);

    /// <summary>
    /// Creates a notification for a deleted entity.
    /// </summary>
    public static DataChangeNotification Deleted(string path, object? entity = null) =>
        new(NormalizePath(path), DataChangeKind.Deleted, entity, DateTimeOffset.UtcNow);

    private static string NormalizePath(string? path) =>
        path?.Trim('/').ToLowerInvariant() ?? "";
}

/// <summary>
/// Types of data changes that can occur.
/// </summary>
public enum DataChangeKind
{
    /// <summary>A new entity was created.</summary>
    Created,

    /// <summary>An existing entity was updated.</summary>
    Updated,

    /// <summary>An entity was deleted.</summary>
    Deleted
}
