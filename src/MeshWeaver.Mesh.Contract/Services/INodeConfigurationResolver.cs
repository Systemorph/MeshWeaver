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
    /// Enriches a MeshNode with its NodeType's HubConfiguration.
    /// Triggers compilation if needed.
    /// </summary>
    Task<MeshNode> ResolveConfigurationAsync(MeshNode node, CancellationToken ct = default);
}
