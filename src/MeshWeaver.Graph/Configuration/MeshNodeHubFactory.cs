using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Resolves HubConfiguration for a MeshNode via
/// <see cref="NodeTypeEnrichmentHelpers.EnrichWithNodeType"/>, then composes
/// with <c>DefaultNodeHubConfiguration</c>. Stateless — the persisted
/// NodeType MeshNode is the cache.
/// </summary>
internal class MeshNodeHubFactory(
    IMessageHub meshHub,
    MeshConfiguration meshConfiguration,
    IMeshNodeCompilationService? compilationService,
    ILogger<MeshNodeHubFactory> logger) : IMeshNodeHubFactory
{
    public IObservable<MeshNode> ResolveHubConfiguration(MeshNode node)
        => NodeTypeEnrichmentHelpers.EnrichWithNodeType(
                meshHub, meshConfiguration, compilationService, node, logger)
            .Take(1)
            .Select(enriched =>
            {
                var defaultConfig = meshConfiguration.DefaultNodeHubConfiguration;
                if (defaultConfig != null)
                {
                    var nodeConfig = enriched.HubConfiguration;
                    enriched = enriched with
                    {
                        HubConfiguration = nodeConfig != null
                            ? (Func<MessageHubConfiguration, MessageHubConfiguration>)(config => nodeConfig(defaultConfig(config)))
                            : defaultConfig
                    };
                }

                if (enriched.HubConfiguration == null)
                {
                    logger.LogWarning("No HubConfiguration resolved for node {Path} (NodeType: {NodeType})",
                        enriched.Path, enriched.NodeType);
                }

                // 🚨 The binding above is made ONCE and then PINNED by address: routing
                // short-circuits on GetHostedHub for an already-hosted address and never resolves
                // the path again, so nothing re-reads the NodeType for the hub's whole lifetime.
                // Arm the rebind watcher HERE — the single funnel every activation path (Monolith
                // routing AND MessageHubGrain) goes through — so a node that acquires or changes
                // its type recycles its hub instead of serving the wrong configuration forever
                // (issue #1104). See NodeTypeRebindWatcher for why the mesh change feed, not the
                // hub's own node stream, is the signal.
                return NodeTypeRebindWatcher.WithNodeTypeRebind(enriched, meshHub, logger);
            });
}
