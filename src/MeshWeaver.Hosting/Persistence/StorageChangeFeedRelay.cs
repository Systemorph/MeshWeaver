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
/// <para>The relay is owned by the mesh-scoped <see cref="InProcessMeshChangeFeed"/> and dies with
/// it. A notification that carries what the consumers need — the node itself, or the
/// <see cref="DataChangeNotification.NodeType"/> + <see cref="DataChangeNotification.Version"/>
/// hints, or a delete — is relayed at once, in arrival order. A create/update that carries none of
/// it (the mixed-version rollout: an older backend payload with only path/op, or a backend-private
/// descriptor) needs one authoritative storage read before a consumer that filters on type sees
/// it.</para>
///
/// <para>🚨 That read goes through <see cref="ReReadCoalescing"/> — THE coalescer the per-node hub's
/// own reconcile in <c>MeshDataSource</c> already uses — per path: a burst of notifications on one
/// path collapses to at most one read per quiet <see cref="ReReadCoalescing.Window"/>, the LAST
/// notification of a burst always reads, and the reads on a path are serialised. The relay once
/// read ahead of that coalescer, once per notification, and a burst of 200 entity-less
/// notifications on one path cost 201 reads instead of a handful — a notification storm turned
/// into a read storm on every replica (#4139, the shape #223 guards against). A path's group
/// lives the quiet window plus the read bound past its last notification — long enough for its
/// coalesced read to be over, so a burst that lands during that read queues behind it instead of
/// racing it — and then closes, so an idle process holds no state per path ever notified. A read
/// that fails or stays silent past its bound still emits a path-only version-zero invalidation,
/// so one backend fault cannot leave the replica's exact-path cache untouched; a read that finds
/// NO row emits nothing — the delete that removed it was self-contained and relayed at once.</para>
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
            .Select(Classify)
            .Where(arrival => arrival.Path.Length > 0)
            .Publish(arrivals => arrivals
                // Self-contained: relayed in arrival order, nothing to wait for.
                .Where(arrival => !arrival.NeedsCompatibilityRead)
                .SelectMany(arrival => Observable.Defer(() =>
                        Observable.Return(ToMeshChange(arrival.Notification, arrival.Node)))
                    .Catch((Exception ex) => PathOnlyFallback(arrival.Notification, ex)))
                // Entity-less create/update without hints: ONE coalesced read per path per quiet
                // window, never one per notification. A path's group lives (quiet window + read
                // bound) past its last notification: the coalesced read starts at the window and
                // is over — answered, faulted or timed out — by the bound, so a notification that
                // arrives while a read is in flight joins the SAME group and queues behind it.
                // Reads on a path are serialised by construction, and a path that fell silent
                // holds no state.
                .Merge(arrivals
                    .Where(arrival => arrival.NeedsCompatibilityRead)
                    .GroupByUntil(
                        arrival => arrival.Path,
                        group => group.Throttle(
                            ReReadCoalescing.Window + this.legacyReadTimeout, this.scheduler),
                        StringComparer.OrdinalIgnoreCase)
                    .SelectMany(group => group.CoalesceReReads(ReadThenResolve, this.scheduler))))
            .Subscribe(
                PublishSafely,
                ex => logger?.LogError(ex,
                    "Storage change-feed relay stopped — this process will no longer receive "
                    + "storage-backed cache invalidations"));
    }

    /// <summary>
    /// One notification as it arrived, classified ONCE: the path it names, the node its entity
    /// carries (when it carries one), and whether a compatibility read is owed.
    /// </summary>
    private readonly record struct Arrival(DataChangeNotification Notification, string Path, MeshNode? Node)
    {
        /// <summary>
        /// A create/update whose entity is not the node and whose hints are incomplete. During the
        /// additive rollout an older backend payload carries only path/op, and the PostgreSQL
        /// listener carries a backend-private descriptor; a consumer that filters on NodeType
        /// (<c>NodeTypeRebindWatcher</c>) or gates on Version (the remote-stream resubscribe) must
        /// never see an "unknown" create/update merely because the notifier was upgraded second.
        /// </summary>
        public bool NeedsCompatibilityRead =>
            Node is null
            && Notification.Kind is DataChangeKind.Created or DataChangeKind.Updated
            && (string.IsNullOrEmpty(Notification.NodeType) || !Notification.Version.HasValue);
    }

    private Arrival Classify(DataChangeNotification notification)
        // No logger on purpose: a foreign entity is the DESIGNED shape of the PostgreSQL feed (a
        // ChangedNodeDescriptor, never a MeshNode), so "not convertible" here is a classification,
        // not a fault — with a logger it was one Error line per NOTIFY per replica. A payload that
        // fails to parse is not lost: it takes the coalesced read, and THAT logs when it fails.
        => new(notification, NormalizePath(notification.Path),
            notification.Entity.As<MeshNode>(readOptions));

    private IObservable<MeshChangeEvent> ReadThenResolve(Arrival arrival)
        => Observable.Defer(() => storage.Read(arrival.Path, readOptions)
                .Take(1)
                .Timeout(legacyReadTimeout, scheduler)
                // No row = the row is gone: a delete landed between the notification and this
                // read, and THAT delete is self-contained, so it was relayed at once — ahead of
                // this read. Nothing to say here (the same rule as the per-node hub's own
                // reconcile): a Created/Updated carrying no node and no version AFTER the
                // Deleted would read as a retype to "(none)" to NodeTypeRebindWatcher and recycle
                // a hub the delete is already tearing down.
                .Where(nodeAtCommit => nodeAtCommit is not null)
                .Select(nodeAtCommit => ToMeshChange(arrival.Notification, nodeAtCommit)))
            // A read that FAULTS or stays silent past its bound is a different case: the row's
            // state is unknown, so the path is still invalidated, without node or version.
            // Inside the coalescer's serialised queue the fault must resolve to a value or to
            // nothing — never propagate — or the queue terminates and this path stops relaying.
            .Catch((Exception ex) => PathOnlyFallback(arrival.Notification, ex));

    private IObservable<MeshChangeEvent> PathOnlyFallback(
        DataChangeNotification notification, Exception ex)
    {
        logger?.LogWarning(ex,
            "Storage change-feed relay could not enrich {Kind} {Path}; "
            + "publishing a path-only invalidation and continuing",
            notification.Kind, notification.Path);
        return TryPathOnly(notification, out var fallback)
            ? Observable.Return(fallback)
            : Observable.Empty<MeshChangeEvent>();
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
