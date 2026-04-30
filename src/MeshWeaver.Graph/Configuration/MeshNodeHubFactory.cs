using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Resolves HubConfiguration for a MeshNode by:
/// 1. Enriching via <see cref="INodeTypeService.EnrichWithNodeType"/>
///    (triggers compilation if needed; itself reactive).
/// 2. Composing the result with <c>DefaultNodeHubConfiguration</c> so per-node
///    hubs inherit cross-cutting concerns (security pipeline, layout areas).
///
/// <para>The implementation is reactive end-to-end — no <c>await</c>, no Task
/// bridge inside this method. The previous shape <c>await … .FirstAsync().ToTask()</c>
/// captured the caller's synchronization context (typically a hub action
/// block or Orleans grain scheduler). The compile chain posts CompileRequest
/// messages routed through the same RoutingService that's currently
/// processing the inbound delivery — the action block was already blocked
/// waiting for ResolveHubConfigurationAsync to return, so the compile
/// response had nowhere to land. Returning <see cref="IObservable{T}"/>
/// lets the caller decide its scheduling: a hub-flow caller can
/// <c>Subscribe</c>; a framework boundary (Orleans grain activation,
/// ASP.NET request) bridges once with <c>.FirstAsync().ToTask(ct)</c>.</para>
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
                // Compose with DefaultNodeHubConfiguration. The node's
                // HubConfiguration (from built-in type or compilation) is
                // applied ON TOP of the default.
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
