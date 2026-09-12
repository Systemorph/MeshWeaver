using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Relays the storage adapter's process-local <see cref="IStorageAdapter.Changes"/> stream into
/// the process-local mesh change feed. Durable backends such as PostgreSQL feed every process's
/// adapter from their database change channel, so this gives every replica its own cache
/// invalidation without relying on one cluster-singleton Orleans grain to reach process memory.
/// </summary>
/// <remarks>
/// The relay is owned by the mesh-scoped <see cref="InProcessMeshChangeFeed"/> and dies with it.
/// Missing create/update metadata is resolved by one authoritative storage read before the event
/// reaches type-filtering consumers. Reads remain reactive and are serialised with
/// <see cref="Observable.Concat{TSource}(IObservable{IObservable{TSource}})"/> so notification order
/// is preserved while a backend I/O leaf is in flight.
/// </remarks>
internal sealed class StorageChangeFeedRelay : IDisposable
{
    private readonly IStorageAdapter storage;
    private readonly Action<MeshChangeEvent> publishLocal;
    private readonly ILogger? logger;
    private readonly JsonSerializerOptions readOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };
    private readonly IDisposable subscription;

    public StorageChangeFeedRelay(
        IStorageAdapter storage,
        Action<MeshChangeEvent> publishLocal,
        ILogger? logger = null)
    {
        this.storage = storage;
        this.publishLocal = publishLocal;
        this.logger = logger;

        subscription = storage.Changes
            // Defer the whole conversion, not only the optional storage read. A malformed
            // notification (for example an enum value from a newer backend) can throw while
            // Resolve builds its observable; without Defer that synchronous throw terminates the
            // outer Changes subscription and this replica misses every later commit.
            .Select(notification => Observable.Defer(() => Resolve(notification))
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex,
                        "Storage change-feed relay could not resolve {Kind} {Path}; "
                        + "the notification was not published",
                        notification.Kind, notification.Path);
                    return Observable.Empty<MeshChangeEvent>();
                }))
            .Concat()
            .Subscribe(
                PublishSafely,
                ex => logger?.LogError(ex,
                    "Storage change-feed relay stopped — this process will no longer receive "
                    + "storage-backed cache invalidations"));
    }

    private IObservable<MeshChangeEvent> Resolve(DataChangeNotification notification)
    {
        var path = notification.Path.Trim('/');
        if (string.IsNullOrEmpty(path))
            return Observable.Empty<MeshChangeEvent>();

        if (notification.Entity is MeshNode node)
            return Observable.Return(ToMeshChange(notification, node));

        if (notification.Kind == DataChangeKind.Deleted
            || (!string.IsNullOrEmpty(notification.NodeType) && notification.Version.HasValue))
            return Observable.Return(ToMeshChange(notification, null));

        // During the additive rollout an older backend payload carries only path/op (or a
        // backend-private descriptor). Re-read once so consumers that filter by NodeType never
        // see an "unknown" create/update merely because the notifier was upgraded second.
        return storage.Read(path, readOptions)
            .Take(1)
            .Select(nodeAtCommit => ToMeshChange(notification, nodeAtCommit));
    }

    private void PublishSafely(MeshChangeEvent change)
    {
        try
        {
            publishLocal(change);
        }
        catch (Exception ex)
        {
            // IStorageAdapter.Changes is a shared, isolated fan-out. Never let one mesh-feed
            // subscriber tear this relay out of that source and starve every later notification.
            logger?.LogError(ex,
                "Storage change-feed relay subscriber failed for {Kind} {Path}; continuing",
                change.Kind, change.Path);
        }
    }

    private static MeshChangeEvent ToMeshChange(
        DataChangeNotification notification,
        MeshNode? node)
    {
        var path = notification.Path.Trim('/');
        var (fallbackNamespace, fallbackId) = SplitPath(path);
        var kind = notification.Kind switch
        {
            DataChangeKind.Created => MeshChangeKind.Created,
            DataChangeKind.Updated => MeshChangeKind.Updated,
            DataChangeKind.Deleted => MeshChangeKind.Deleted,
            _ => throw new ArgumentOutOfRangeException(
                nameof(notification), notification.Kind, "Unknown storage change kind"),
        };

        return new MeshChangeEvent(
            node?.Namespace ?? fallbackNamespace,
            node?.Id ?? fallbackId,
            path,
            kind,
            notification.NodeType ?? node?.NodeType,
            notification.Version ?? node?.Version ?? 0,
            notification.Timestamp);
    }

    private static (string Namespace, string Id) SplitPath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0
            ? (string.Empty, path)
            : (path[..separator], path[(separator + 1)..]);
    }

    public void Dispose() => subscription.Dispose();
}
