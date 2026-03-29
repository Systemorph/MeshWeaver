using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Resolves HubConfiguration for a MeshNode by:
/// 1. Using existing HubConfiguration if already set
/// 2. Enriching via INodeTypeService (triggers compilation if needed)
/// 3. Composing with DefaultNodeHubConfiguration
/// </summary>
internal class MeshNodeHubFactory(
    INodeTypeService nodeTypeService,
    ILogger<MeshNodeHubFactory> logger) : IMeshNodeHubFactory
{
    public async Task<MeshNode> ResolveHubConfigurationAsync(MeshNode node, CancellationToken ct = default)
    {
        // Already fully resolved
        if (node.HubConfiguration != null)
            return node;

        // Enrich with node type (triggers compilation if needed)
        // EnrichWithNodeTypeAsync → GetCachedHubConfiguration already composes with DefaultNodeHubConfiguration
        node = await nodeTypeService.EnrichWithNodeTypeAsync(node, ct);

        if (node.HubConfiguration == null)
        {
            logger.LogWarning("No HubConfiguration resolved for node {Path} (NodeType: {NodeType})",
                node.Path, node.NodeType);
        }

        return node;
    }
}
