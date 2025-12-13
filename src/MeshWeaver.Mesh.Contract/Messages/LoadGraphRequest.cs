using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Messages;

/// <summary>
/// Request to load persisted graph data for a node.
/// Sent to persistent hubs on startup to load children from IPersistenceService.
/// </summary>
/// <param name="Node">The mesh node to load data for</param>
public record LoadGraphRequest(MeshNode Node) : IRequest<LoadGraphResponse>;

/// <summary>
/// Response containing the loaded child nodes.
/// </summary>
/// <param name="Children">The loaded child nodes</param>
public record LoadGraphResponse(IReadOnlyList<MeshNode> Children);
