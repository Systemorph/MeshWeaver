using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Per-namespace lookup for <see cref="PartitionDefinition"/>s, backed by the
/// live workspace stream and cached via <c>Replay(1).RefCount()</c>. Callers
/// subscribe to <see cref="GetPartition"/> to read the current definition for
/// a namespace and stay subscribed to receive updates when the partition's
/// config changes.
///
/// <para>Used by <see cref="CodeNodeType.HandleExecuteScript"/> to resolve
/// <see cref="PartitionDefinition.DefaultActivityParentPath"/> reactively —
/// without forcing the sync handler to bridge an async catalog query.</para>
/// </summary>
public sealed class PartitionRegistry
{
    private readonly IMessageHub hub;
    private readonly ILogger<PartitionRegistry>? logger;
    private readonly ConcurrentDictionary<string, IObservable<PartitionDefinition?>> cache = new();

    /// <summary>
    /// Initializes a new instance of the partition registry bound to the given hub.
    /// </summary>
    /// <param name="hub">The message hub used to resolve the mesh service and query partitions.</param>
    /// <param name="loggerFactory">An optional logger factory used to log lookup failures.</param>
    public PartitionRegistry(IMessageHub hub, ILoggerFactory? loggerFactory = null)
    {
        this.hub = hub;
        this.logger = loggerFactory?.CreateLogger<PartitionRegistry>();
    }

    /// <summary>
    /// Live <see cref="PartitionDefinition"/> for <paramref name="namespacePrefix"/>
    /// (the partition's first-segment namespace, e.g. <c>"Doc"</c>, <c>"rbuergi"</c>).
    /// Emits the current definition immediately on subscribe (or null if none),
    /// then re-emits whenever the partition's config changes. Backed by
    /// <c>Replay(1).RefCount()</c> so multiple subscribers share a single
    /// upstream subscription and a fresh subscriber gets the cached snapshot
    /// without round-tripping the workspace.
    /// </summary>
    /// <remarks>
    /// 🚨 A fault is never CACHED. It used to become a <c>null</c> held by the shared
    /// <c>Replay(1)</c> entry for the life of the process, so one provider stall answered "this
    /// partition has no definition" to every later caller until a restart. Now the faulted entry is
    /// EVICTED as it answers its current subscribers (null, at Warning — the lookup's documented
    /// degradation), and the next <see cref="GetPartition"/> opens a fresh query.
    /// </remarks>
    public IObservable<PartitionDefinition?> GetPartition(string namespacePrefix) =>
        cache.GetOrAdd(namespacePrefix, ns =>
        {
            // Look up the Partition node by walking Admin/Partition/* — its
            // PartitionDefinition.Namespace must equal the requested ns.
            // Workspace query is reactive, so this stays live as partitions
            // are added or updated at runtime (rare, but possible).
            IObservable<PartitionDefinition?>? entry = null;
            entry = Observable.Defer(OpenQueryOverride ?? OpenPartitionQuery)
                // Fold the change feed: Initial/Reset carry the whole set, Added/Updated/Removed
                // are DELTAS — reading a delta as the whole set answered null for this namespace
                // whenever an unrelated partition changed.
                .Scan(ImmutableDictionary<string, MeshNode>.Empty, (set, change) => change.ChangeType switch
                {
                    QueryChangeType.Initial or QueryChangeType.Reset =>
                        ImmutableDictionary<string, MeshNode>.Empty.SetItems(Keyed(change.Items)),
                    QueryChangeType.Removed => set.RemoveRange(Keyed(change.Items).Select(kv => kv.Key)),
                    _ => set.SetItems(Keyed(change.Items)),
                })
                .Select(set => set.Values
                    .Select(node => node.ContentAs<PartitionDefinition>(hub.JsonSerializerOptions, logger))
                    .FirstOrDefault(def => def is not null
                        && string.Equals(def.Namespace, ns, StringComparison.Ordinal)))
                .DistinctUntilChanged()
                .Catch<PartitionDefinition?, Exception>(ex =>
                {
                    // Evict THIS entry (never a newer one another caller installed), so the fault
                    // ends with its current subscribers instead of being replayed to every later one.
                    cache.TryRemove(new KeyValuePair<string, IObservable<PartitionDefinition?>>(ns, entry!));
                    logger?.LogWarning(ex,
                        "PartitionRegistry: lookup for {Namespace} failed; treating as no definition for "
                        + "this caller — the next lookup re-opens the query.",
                        ns);
                    return Observable.Return<PartitionDefinition?>(null);
                })
                .Replay(1)
                .RefCount();
            return entry;
        });

    /// <summary>Test seam: the partition query. Null in production.</summary>
    internal Func<IObservable<QueryResultChange<MeshNode>>>? OpenQueryOverride { get; set; }

    private IObservable<QueryResultChange<MeshNode>> OpenPartitionQuery() =>
        hub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(new MeshQueryRequest
            {
                Query = "namespace:Admin/Partition nodeType:Partition",
                Skip = 0,
                Limit = 100
            });

    private static IEnumerable<KeyValuePair<string, MeshNode>> Keyed(IEnumerable<MeshNode>? nodes) =>
        (nodes ?? []).Where(n => !string.IsNullOrEmpty(n?.Path))
            .Select(n => new KeyValuePair<string, MeshNode>(n.Path, n));
}
