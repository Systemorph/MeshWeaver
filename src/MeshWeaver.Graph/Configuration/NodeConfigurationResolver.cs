using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Resolves hub configuration for MeshNodes by delegating to <see cref="INodeTypeService.EnrichWithNodeType"/>.
/// Registered as singleton. Pure observable surface — no Task bridge, no <c>.ToTask()</c>; callers
/// Subscribe on the hub dispatcher. The legacy <c>Task</c>-returning interface
/// <c>ResolveConfigurationAsync</c> is gone — every caller observes directly.
/// </summary>
internal class NodeConfigurationResolver(INodeTypeService nodeTypeService) : INodeConfigurationResolver
{
    public IObservable<MeshNode> ResolveConfiguration(MeshNode node)
        => nodeTypeService.EnrichWithNodeType(node);
}
