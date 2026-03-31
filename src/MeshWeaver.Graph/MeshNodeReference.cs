using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// Workspace reference that returns the hub's own MeshNode.
/// Used for typed stream access with proper write-back support.
/// </summary>
public record MeshNodeReference() : WorkspaceReference<MeshNode>;
