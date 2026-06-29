using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Resolves hub configuration for a MeshNode via
/// <see cref="NodeTypeEnrichmentHelpers.EnrichWithNodeType"/>: stateless,
/// reads the live NodeType MeshNode through the shared
/// <see cref="IMeshNodeStreamCache"/>. No INodeTypeService caches.
/// </summary>
internal class NodeConfigurationResolver(
    IMessageHub meshHub,
    MeshConfiguration meshConfiguration,
    IMeshNodeCompilationService? compilationService = null,
    ILogger<NodeConfigurationResolver>? logger = null) : INodeConfigurationResolver
{
    public IObservable<MeshNode> ResolveConfiguration(MeshNode node)
        => NodeTypeEnrichmentHelpers.EnrichWithNodeType(
            meshHub, meshConfiguration, compilationService, node, logger);
}
