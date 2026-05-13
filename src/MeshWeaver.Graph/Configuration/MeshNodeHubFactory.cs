using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Resolves HubConfiguration for a MeshNode via
/// <see cref="NodeTypeEnrichmentHelpers.EnrichWithNodeType"/> (static fast-path
/// for built-in types; Activity-hub-driven stream slow-path for dynamic types).
/// Composes the result with <c>DefaultNodeHubConfiguration</c> so per-node
/// hubs inherit cross-cutting concerns (security pipeline, layout areas).
/// </summary>
internal class MeshNodeHubFactory(
    IMessageHub hub,
    MeshConfiguration meshConfiguration,
    NodeTypeServiceHub serviceHub,
    ILogger<MeshNodeHubFactory> logger) : IMeshNodeHubFactory
{
    private readonly IMeshNodeCompilationService? _compilationService =
        hub.ServiceProvider.GetService<IMeshNodeCompilationService>();

    public IObservable<MeshNode> ResolveHubConfiguration(MeshNode node)
        => NodeTypeEnrichmentHelpers.EnrichWithNodeType(
                serviceHub.Hub, meshConfiguration, _compilationService, node, logger)
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
