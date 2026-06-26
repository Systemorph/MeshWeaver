using System.Collections.Concurrent;
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
    public IObservable<PartitionDefinition?> GetPartition(string namespacePrefix) =>
        cache.GetOrAdd(namespacePrefix, ns =>
        {
            // Look up the Partition node by walking Admin/Partition/* — its
            // PartitionDefinition.Namespace must equal the requested ns.
            // Workspace query is reactive, so this stays live as partitions
            // are added or updated at runtime (rare, but possible).
            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            return meshService
                .Query<MeshNode>(new MeshQueryRequest
                {
                    Query = "namespace:Admin/Partition nodeType:Partition",
                    Skip = 0,
                    Limit = 100
                })
                .Select(c =>
                {
                    foreach (var node in c.Items)
                    {
                        if (node.Content is PartitionDefinition def
                            && string.Equals(def.Namespace, ns, StringComparison.Ordinal))
                            return (PartitionDefinition?)def;
                    }
                    return null;
                })
                .Catch<PartitionDefinition?, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "PartitionRegistry: lookup for {Namespace} failed; treating as no definition.",
                        ns);
                    return Observable.Return<PartitionDefinition?>(null);
                })
                .Replay(1)
                .RefCount();
        });
}
