using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Resolves hub configuration for a MeshNode via
/// <see cref="NodeTypeEnrichmentHelpers.EnrichWithNodeType"/>: stateless,
/// reads the live NodeType MeshNode through the dedicated
/// <see cref="NodeTypeServiceHub"/> workspace. No INodeTypeService caches.
/// </summary>
internal class NodeConfigurationResolver(
    NodeTypeServiceHub serviceHub,
    MeshConfiguration meshConfiguration,
    IMeshNodeCompilationService? compilationService = null,
    ILogger<NodeConfigurationResolver>? logger = null) : INodeConfigurationResolver
{
    public IObservable<MeshNode> ResolveConfiguration(MeshNode node)
        => NodeTypeEnrichmentHelpers.EnrichWithNodeType(
            serviceHub.Hub, meshConfiguration, compilationService, node, logger);
}
