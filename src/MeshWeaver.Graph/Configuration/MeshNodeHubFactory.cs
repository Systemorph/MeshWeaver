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
    NodeTypeServiceHub serviceHub,
    MeshConfiguration meshConfiguration,
    IMeshNodeCompilationService? compilationService,
    ILogger<MeshNodeHubFactory> logger) : IMeshNodeHubFactory
{
    public IObservable<MeshNode> ResolveHubConfiguration(MeshNode node)
        => NodeTypeEnrichmentHelpers.EnrichWithNodeType(
                serviceHub.Hub, meshConfiguration, compilationService, node, logger)
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

                return enriched;
            });
}
