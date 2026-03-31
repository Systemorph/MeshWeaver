using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Resolves hub configuration for MeshNodes by delegating to INodeTypeService.
/// Registered as singleton — does NOT depend on MeshCatalog.
/// </summary>
internal class NodeConfigurationResolver(INodeTypeService nodeTypeService) : INodeConfigurationResolver
{
    public Task<MeshNode> ResolveConfigurationAsync(MeshNode node, CancellationToken ct = default)
        => nodeTypeService.EnrichWithNodeTypeAsync(node, ct);
}
