using System.Reactive.Linq;
using System.Reactive.Concurrency;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Relays the storage adapter's process-local <see cref="IStorageAdapter.Changes"/> stream into
/// the process-local <see cref="IMeshInvalidationFeed"/>. Durable backends such as PostgreSQL feed
/// every process's adapter from their database change channel, so this gives every replica its own
/// cache invalidation without relying on one cluster-singleton Orleans grain to reach process
/// memory or re-running logical event consumers on every replica.
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
    private static readonly TimeSpan DefaultLegacyReadTimeout = TimeSpan.FromSeconds(5);
    private readonly IStorageAdapter storage;
    private readonly Action<MeshChangeEvent> publishLocal;
    private readonly ILogger? logger;
    private readonly TimeSpan legacyReadTimeout;
    private readonly IScheduler scheduler;
    private readonly JsonSerializerOptions readOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IDisposable subscription;

    public StorageChangeFeedRelay(
        IStorageAdapter storage,
        Action<MeshChangeEvent> publishLocal,
        ILogger? logger = null,
        TimeSpan? legacyReadTimeout = null,
        IScheduler? scheduler = null)
    {
        this.storage = storage;
        this.publishLocal = publishLocal;
        this.logger = logger;
        this.legacyReadTimeout = legacyReadTimeout ?? DefaultLegacyReadTimeout;
        this.scheduler = scheduler ?? Scheduler.Default;

        subscription = storage.Changes
            // Defer the whole conversion, not only the optional storage read. A malformed
            // notification (for example an enum value from a newer backend) can throw while
            // Resolve builds its observable; without Defer that synchronous throw terminates the
            // outer Changes subscription and this replica misses every later commit.
            .Select(notification => Observable.Defer(() => Resolve(notification))
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex,
                        "Storage change-feed relay could not enrich {Kind} {Path}; "
                        + "publishing a path-only invalidation and continuing",
                        notification.Kind, notification.Path);
                    return TryPathOnly(notification, out var fallback)
                        ? Observable.Return(fallback)
                        : Observable.Empty<MeshChangeEvent>();
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
        var path = NormalizePath(notification.Path);
        if (string.IsNullOrEmpty(path))
            return Observable.Empty<MeshChangeEvent>();

        var node = notification.Entity.As<MeshNode>(
            readOptions, logger, $"storage notification {path}");
        if (node is not null)
            return Observable.Return(ToMeshChange(notification, node));

        if (notification.Kind == DataChangeKind.Deleted
            || (!string.IsNullOrEmpty(notification.NodeType) && notification.Version.HasValue))
            return Observable.Return(ToMeshChange(notification, null));

        // During the additive rollout an older backend payload carries only path/op (or a
        // backend-private descriptor). Re-read once so consumers that filter by NodeType never
        // see an "unknown" create/update merely because the notifier was upgraded second.
        return storage.Read(path, readOptions)
            .Take(1)
            .Timeout(legacyReadTimeout, scheduler)
            .DefaultIfEmpty(null)
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
        var path = NormalizePath(notification.Path);
        var (fallbackNamespace, fallbackId) = SplitPath(path);
        var kind = notification.Kind switch
        {
            DataChangeKind.Created => MeshChangeKind.Created,
            DataChangeKind.Updated => MeshChangeKind.Updated,
            DataChangeKind.Deleted => MeshChangeKind.Deleted,
            _ => throw new ArgumentOutOfRangeException(
                nameof(notification), notification.Kind, "Unknown storage change kind"),
        };

        var version = kind == MeshChangeKind.Deleted
            ? 0
            : notification.Version ?? node?.Version ?? 0;
        var nodeType = string.IsNullOrEmpty(notification.NodeType)
            ? node?.NodeType
            : notification.NodeType;
        return new MeshChangeEvent(
            node?.Namespace ?? fallbackNamespace,
            node?.Id ?? fallbackId,
            path,
            kind,
            nodeType,
            version,
            notification.Timestamp);
    }

    private static bool TryPathOnly(
        DataChangeNotification notification,
        out MeshChangeEvent change)
    {
        var path = NormalizePath(notification.Path);
        if (string.IsNullOrEmpty(path)
            || notification.Kind is not (DataChangeKind.Created
                or DataChangeKind.Updated
                or DataChangeKind.Deleted))
        {
            change = default!;
            return false;
        }

        change = ToMeshChange(notification, null);
        return true;
    }

    private static (string Namespace, string Id) SplitPath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0
            ? (string.Empty, path)
            : (path[..separator], path[(separator + 1)..]);
    }

    private static string NormalizePath(string? path) => path?.Trim('/') ?? string.Empty;

    public void Dispose() => subscription.Dispose();
}
