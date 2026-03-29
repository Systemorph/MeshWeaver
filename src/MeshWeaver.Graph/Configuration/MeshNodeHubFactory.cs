using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Resolves HubConfiguration for a MeshNode by:
/// 1. Enriching via INodeTypeService (triggers compilation if needed)
/// 2. Composing with DefaultNodeHubConfiguration
/// </summary>
internal class MeshNodeHubFactory(
    MeshConfiguration meshConfiguration,
    INodeTypeService nodeTypeService,
    ILogger<MeshNodeHubFactory> logger) : IMeshNodeHubFactory
{
    public async Task<MeshNode> ResolveHubConfigurationAsync(MeshNode node, CancellationToken ct = default)
    {
        // Enrich with node type (triggers compilation if needed, sets HubConfiguration)
        node = await nodeTypeService.EnrichWithNodeTypeAsync(node, ct);

        // Compose with DefaultNodeHubConfiguration.
        // The node's HubConfiguration (from built-in type or compilation) is applied ON TOP of the default.
        var defaultConfig = meshConfiguration.DefaultNodeHubConfiguration;
        if (defaultConfig != null)
        {
            var nodeConfig = node.HubConfiguration;
            node = node with
            {
                HubConfiguration = nodeConfig != null
                    ? (Func<MessageHubConfiguration, MessageHubConfiguration>)(config => nodeConfig(defaultConfig(config)))
                    : defaultConfig
            };
        }

        if (node.HubConfiguration == null)
        {
            logger.LogWarning("No HubConfiguration resolved for node {Path} (NodeType: {NodeType})",
                node.Path, node.NodeType);
        }

        return node;
    }
}
