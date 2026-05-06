using System.Collections.Concurrent;
using System.Reactive.Linq;

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
    /// Monotonic per-row version emitted by the storage layer (e.g. PostgreSQL
    /// <c>mesh_nodes.version</c>). Used by subscribers to dedupe local-write
    /// echoes: when the same process both writes a row AND receives the LISTEN/NOTIFY
    /// echo of that write, the echo carries the same Version as the in-process
    /// emission and can be dropped via <c>DistinctUntilChanged</c> / a per-path
    /// last-seen-version cache. <c>-1</c> means "no version available" (in-memory
    /// adapters that don't track versions, or DELETE events where no NEW row exists).
    /// </summary>
    public long Version { get; init; } = -1;

    /// <summary>
    /// Creates a notification for a created entity.
    /// </summary>
    public static DataChangeNotification Created(string path, object? entity, long version = -1) =>
        new(NormalizePath(path), DataChangeKind.Created, entity, DateTimeOffset.UtcNow) { Version = version };

    /// <summary>
    /// Creates a notification for an updated entity.
    /// </summary>
    public static DataChangeNotification Updated(string path, object? entity, long version = -1) =>
        new(NormalizePath(path), DataChangeKind.Updated, entity, DateTimeOffset.UtcNow) { Version = version };

    /// <summary>
    /// Creates a notification for a deleted entity.
    /// </summary>
    public static DataChangeNotification Deleted(string path, object? entity = null, long version = -1) =>
        new(NormalizePath(path), DataChangeKind.Deleted, entity, DateTimeOffset.UtcNow) { Version = version };

    private static string NormalizePath(string? path) =>
        path?.Trim('/') ?? "";
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

/// <summary>
/// Reactive helpers for <see cref="DataChangeNotification"/> streams.
/// </summary>
public static class DataChangeNotificationExtensions
{
    /// <summary>
    /// Drops local-write echoes from the source stream. When the same process both
    /// publishes a change in-process (e.g. <c>InMemoryPersistenceService.SaveNode</c>
    /// emitting Created/Updated to <see cref="IDataChangeNotifier"/>) AND receives the
    /// LISTEN/NOTIFY echo of that same write (PG-backed adapter publishes a second
    /// notification when its trigger fires), both notifications carry the same
    /// <c>(Path, Version)</c> tuple. This operator keeps a per-path last-seen-version
    /// table and drops any subsequent notification whose <c>Version</c> is &lt;= the
    /// last seen for that path.
    /// <para>Notifications with <see cref="DataChangeNotification.Version"/> = <c>-1</c>
    /// (storage layer has no version, or a DELETE event with no NEW row) bypass the
    /// dedup — they always pass through.</para>
    /// </summary>
    public static IObservable<DataChangeNotification> DistinctByPathVersion(
        this IObservable<DataChangeNotification> source)
    {
        var seen = new ConcurrentDictionary<string, long>();
        return source.Where(n =>
        {
            // No version → can't dedupe; always pass through (in-memory adapters,
            // legacy NOTIFY trigger, file-system change watcher).
            if (n.Version < 0)
                return true;

            // Atomic max — if our version is strictly greater than the last seen,
            // emit and update. Otherwise drop as an echo.
            var passed = false;
            seen.AddOrUpdate(
                n.Path,
                _ => { passed = true; return n.Version; },
                (_, last) =>
                {
                    if (n.Version > last)
                    {
                        passed = true;
                        return n.Version;
                    }
                    return last;
                });
            return passed;
        });
    }
}
