using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Resolves hub configuration via <see cref="INodeTypeService.EnrichWithNodeType"/>.
/// Stage 4 of the NodeTypeService deletion still delegates to the service so
/// its internal CreatableTypesRules cache stays populated for consumers
/// downstream (NavigationService, MeshNodeAutocomplete). Once those move off
/// the cache, this resolver will switch to NodeTypeEnrichmentHelpers.
/// </summary>
internal class NodeConfigurationResolver(INodeTypeService nodeTypeService) : INodeConfigurationResolver
{
    public IObservable<MeshNode> ResolveConfiguration(MeshNode node)
        => nodeTypeService.EnrichWithNodeType(node);
}
