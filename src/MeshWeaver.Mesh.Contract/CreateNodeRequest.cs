using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Request to create a new MeshNode in the catalog.
/// The node will be created with Transient state until the hub confirms.
/// </summary>
/// <param name="Node">The MeshNode to create</param>
public record CreateNodeRequest(MeshNode Node) : IRequest<CreateNodeResponse>
{
    /// <summary>
    /// The user or system requesting the creation.
    /// </summary>
    public string? CreatedBy { get; init; }
}

/// <summary>
/// Response for node creation request.
/// </summary>
/// <param name="Node">The created node (with updated State) or null if failed</param>
public record CreateNodeResponse(MeshNode? Node)
{
    /// <summary>
    /// Error message if the creation failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Indicates if the creation was successful.
    /// </summary>
    public bool Success => Error == null && Node != null;

    /// <summary>
    /// The rejection reason if the node was rejected.
    /// </summary>
    public NodeCreationRejectionReason? RejectionReason { get; init; }

    public static CreateNodeResponse Ok(MeshNode node) => new(node);

    public static CreateNodeResponse Fail(string error, NodeCreationRejectionReason reason = NodeCreationRejectionReason.Unknown)
        => new((MeshNode?)null) { Error = error, RejectionReason = reason };
}

/// <summary>
/// Reasons why a node creation request can be rejected.
/// </summary>
public enum NodeCreationRejectionReason
{
    Unknown,
    NodeAlreadyExists,
    InvalidNodeType,
    InvalidPath,
    ValidationFailed
}

/// <summary>
/// Request to delete a MeshNode from the catalog.
/// </summary>
/// <param name="Path">The path of the node to delete</param>
public record DeleteNodeRequest(string Path) : IRequest<DeleteNodeResponse>
{
    /// <summary>
    /// If true, also delete all descendant nodes.
    /// </summary>
    public bool Recursive { get; init; }

    /// <summary>
    /// The user or system requesting the deletion.
    /// </summary>
    public string? DeletedBy { get; init; }
}

/// <summary>
/// Response for node deletion request.
/// </summary>
public record DeleteNodeResponse
{
    /// <summary>
    /// Error message if the deletion failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Indicates if the deletion was successful.
    /// </summary>
    public bool Success => Error == null;

    /// <summary>
    /// The rejection reason if the node deletion was rejected.
    /// </summary>
    public NodeDeletionRejectionReason? RejectionReason { get; init; }

    public static DeleteNodeResponse Ok() => new();

    public static DeleteNodeResponse Fail(string error, NodeDeletionRejectionReason reason = NodeDeletionRejectionReason.Unknown)
        => new() { Error = error, RejectionReason = reason };
}

/// <summary>
/// Reasons why a node deletion request can be rejected.
/// </summary>
public enum NodeDeletionRejectionReason
{
    Unknown,
    NodeNotFound,
    HasChildren,
    ValidationFailed
}

/// <summary>
/// Request to update an existing MeshNode in the catalog.
/// </summary>
/// <param name="Node">The updated MeshNode data</param>
public record UpdateNodeRequest(MeshNode Node) : IRequest<UpdateNodeResponse>
{
    /// <summary>
    /// The user or system requesting the update.
    /// </summary>
    public string? UpdatedBy { get; init; }
}

/// <summary>
/// Response for node update request.
/// </summary>
/// <param name="Node">The updated node or null if failed</param>
public record UpdateNodeResponse(MeshNode? Node)
{
    /// <summary>
    /// Error message if the update failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Indicates if the update was successful.
    /// </summary>
    public bool Success => Error == null && Node != null;

    /// <summary>
    /// The rejection reason if the node update was rejected.
    /// </summary>
    public NodeUpdateRejectionReason? RejectionReason { get; init; }

    public static UpdateNodeResponse Ok(MeshNode node) => new(node);

    public static UpdateNodeResponse Fail(string error, NodeUpdateRejectionReason reason = NodeUpdateRejectionReason.Unknown)
        => new((MeshNode?)null) { Error = error, RejectionReason = reason };
}

/// <summary>
/// Reasons why a node update request can be rejected.
/// </summary>
public enum NodeUpdateRejectionReason
{
    Unknown,
    NodeNotFound,
    ValidationFailed,
    InvalidNodeType,
    ConcurrencyConflict
}
