namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Public factory interface for creating, deleting, and managing mesh node lifecycle.
/// This is the proper API for application-level code to create/delete nodes.
/// For reads, use <see cref="IMeshQuery"/>.
/// For updates, use <see cref="MeshWeaver.Mesh.UpdateNodeRequest"/> via hub.Post().
/// For moves, use <see cref="MeshWeaver.Mesh.MoveNodeRequest"/> via hub.Post().
/// </summary>
public interface IMeshNodeFactory
{
    /// <summary>
    /// Creates a new node with validation.
    /// Routes through CreateNodeRequest for proper security enforcement.
    /// </summary>
    Task<MeshNode> CreateNodeAsync(MeshNode node, string? createdBy = null, CancellationToken ct = default);

    /// <summary>
    /// Creates a transient node for UI creation flows.
    /// The node is persisted in Transient state but NOT confirmed.
    /// Call DeleteNodeAsync to cancel or CreateNodeAsync to confirm.
    /// </summary>
    Task<MeshNode> CreateTransientAsync(MeshNode node, CancellationToken ct = default);

    /// <summary>
    /// Deletes a node.
    /// </summary>
    Task DeleteNodeAsync(string path, bool recursive = false, CancellationToken ct = default);
}
