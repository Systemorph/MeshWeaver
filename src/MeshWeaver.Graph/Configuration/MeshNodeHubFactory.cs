using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Resolves HubConfiguration for a MeshNode via
/// <see cref="INodeTypeService.EnrichWithNodeType"/>, then composes with
/// <c>DefaultNodeHubConfiguration</c>.
///
/// <para>Stage 4 of the NodeTypeService deletion still routes the hot path
/// through INodeTypeService because the service populates internal caches
/// (<c>_creatableTypesRules</c>, <c>_notCreatableTypes</c>) consumed by
/// other code paths. Once those caches are eliminated, this factory will
/// switch to <see cref="NodeTypeEnrichmentHelpers.EnrichWithNodeType"/>.</para>
/// </summary>
internal class MeshNodeHubFactory(
    MeshConfiguration meshConfiguration,
    INodeTypeService nodeTypeService,
    ILogger<MeshNodeHubFactory> logger) : IMeshNodeHubFactory
{
    public IObservable<MeshNode> ResolveHubConfiguration(MeshNode node)
        => nodeTypeService.EnrichWithNodeType(node)
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
