using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Public interface for creating, updating, and deleting mesh nodes.
/// All operations route through the message bus for proper security enforcement via validators.
/// For reads, use <see cref="IMeshQuery"/>.
/// For moves, use <see cref="MeshWeaver.Mesh.MoveNodeRequest"/> via hub.Post().
/// </summary>
public interface IMeshNodePersistence
{
    /// <summary>
    /// Creates a new node with validation.
    /// Routes through CreateNodeRequest for proper security enforcement.
    /// </summary>
    Task<MeshNode> CreateNodeAsync(MeshNode node, string? createdBy = null, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing node with validation.
    /// Routes through UpdateNodeRequest for proper security enforcement.
    /// </summary>
    Task<MeshNode> UpdateNodeAsync(MeshNode node, string? updatedBy = null, CancellationToken ct = default);

    /// <summary>
    /// Deletes a node and all its descendants (bottom to top).
    /// Routes through DeleteNodeRequest for proper security enforcement.
    /// If a descendant cannot be deleted, the parent is not deleted either.
    /// </summary>
    Task DeleteNodeAsync(string path, string? deletedBy = null, CancellationToken ct = default);

    /// <summary>
    /// Creates a transient node for UI creation flows.
    /// The node is persisted in Transient state but NOT confirmed.
    /// Call DeleteNodeAsync to cancel or CreateNodeAsync to confirm.
    /// </summary>
    Task<MeshNode> CreateTransientAsync(MeshNode node, CancellationToken ct = default);

    /// <summary>
    /// Returns a wrapper that sends all operations as the current node's identity
    /// rather than the logged-in user's identity.
    /// Uses PostOptions.ImpersonateAsHub() with the node's own address.
    /// </summary>
    IMeshNodePersistence ImpersonateAsNode();
}
