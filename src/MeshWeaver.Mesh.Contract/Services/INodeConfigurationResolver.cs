using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Resolves hub configuration for a MeshNode based on its NodeType.
/// Separated from MeshCatalog to break circular dependency with INodeTypeService.
/// Used wherever hubs are created from MeshNodes.
/// </summary>
public interface INodeConfigurationResolver
{
    /// <summary>
    /// Enriches a MeshNode with its NodeType's <see cref="MeshNode.HubConfiguration"/>
    /// (and, for dynamic types, the persisted assembly reference fields on
    /// <c>NodeTypeDefinition.LatestAssembly{Collection,Path}</c>). Triggers
    /// compilation if needed. Returns a cold observable that emits exactly one
    /// enriched node — callers Subscribe on the hub dispatcher; no <c>await</c>,
    /// no <c>.ToTask()</c>.
    /// </summary>
    IObservable<MeshNode> ResolveConfiguration(MeshNode node);
}
