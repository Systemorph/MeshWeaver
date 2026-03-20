using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Security;

namespace MeshWeaver.Mesh;

/// <summary>
/// Request to create a new MeshNode in the catalog.
/// The node will be created with Transient state until the hub confirms.
/// </summary>
/// <param name="Node">The MeshNode to create</param>
[RequiresPermission(Permission.Create)]
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

    /// <summary>
    /// Creates a successful response with the created node.
    /// </summary>
    public static CreateNodeResponse Ok(MeshNode node) => new(node);

    /// <summary>
    /// Creates a failed response with an error message.
    /// </summary>
    public static CreateNodeResponse Fail(string error, NodeCreationRejectionReason reason = NodeCreationRejectionReason.Unknown)
        => new((MeshNode?)null) { Error = error, RejectionReason = reason };
}

/// <summary>
/// Reasons why a node creation request can be rejected.
/// </summary>
public enum NodeCreationRejectionReason
{
    /// <summary>
    /// Unknown or unspecified reason.
    /// </summary>
    Unknown,

    /// <summary>
    /// A node with the same path already exists.
    /// </summary>
    NodeAlreadyExists,

    /// <summary>
    /// The specified node type is invalid or not found.
    /// </summary>
    InvalidNodeType,

    /// <summary>
    /// The specified path is invalid.
    /// </summary>
    InvalidPath,

    /// <summary>
    /// Node content validation failed.
    /// </summary>
    ValidationFailed
}

/// <summary>
/// Request to delete a MeshNode from the catalog.
/// </summary>
/// <param name="Path">The path of the node to delete</param>
[RequiresPermission(Permission.Delete)]
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

    /// <summary>
    /// Creates a successful deletion response.
    /// </summary>
    public static DeleteNodeResponse Ok() => new();

    /// <summary>
    /// Creates a failed deletion response with an error message.
    /// </summary>
    public static DeleteNodeResponse Fail(string error, NodeDeletionRejectionReason reason = NodeDeletionRejectionReason.Unknown)
        => new() { Error = error, RejectionReason = reason };
}

/// <summary>
/// Reasons why a node deletion request can be rejected.
/// </summary>
public enum NodeDeletionRejectionReason
{
    /// <summary>
    /// Unknown or unspecified reason.
    /// </summary>
    Unknown,

    /// <summary>
    /// The node to delete was not found.
    /// </summary>
    NodeNotFound,

    /// <summary>
    /// The node has children and recursive deletion was not requested.
    /// </summary>
    HasChildren,

    /// <summary>
    /// Deletion validation failed.
    /// </summary>
    ValidationFailed,

    /// <summary>
    /// A child node could not be deleted, so the parent was not deleted either.
    /// </summary>
    ChildDeletionFailed
}

/// <summary>
/// Request to update an existing MeshNode in the catalog.
/// </summary>
/// <param name="Node">The updated MeshNode data</param>
[RequiresPermission(Permission.Update)]
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

    /// <summary>
    /// Creates a successful update response with the updated node.
    /// </summary>
    public static UpdateNodeResponse Ok(MeshNode node) => new(node);

    /// <summary>
    /// Creates a failed update response with an error message.
    /// </summary>
    public static UpdateNodeResponse Fail(string error, NodeUpdateRejectionReason reason = NodeUpdateRejectionReason.Unknown)
        => new((MeshNode?)null) { Error = error, RejectionReason = reason };
}

/// <summary>
/// Reasons why a node update request can be rejected.
/// </summary>
public enum NodeUpdateRejectionReason
{
    /// <summary>
    /// Unknown or unspecified reason.
    /// </summary>
    Unknown,

    /// <summary>
    /// The node to update was not found.
    /// </summary>
    NodeNotFound,

    /// <summary>
    /// Node content validation failed.
    /// </summary>
    ValidationFailed,

    /// <summary>
    /// The specified node type is invalid or not found.
    /// </summary>
    InvalidNodeType,

    /// <summary>
    /// The node was modified by another process (optimistic concurrency conflict).
    /// </summary>
    ConcurrencyConflict
}

/// <summary>
/// Request to move a MeshNode to a new path.
/// Requires Delete permission on the source namespace and Create permission on the target namespace.
/// </summary>
/// <param name="SourcePath">The current path of the node</param>
/// <param name="TargetPath">The new path for the node</param>
[MoveNodePermission]
public record MoveNodeRequest(string SourcePath, string TargetPath) : IRequest<MoveNodeResponse>;

/// <summary>
/// Custom permission attribute for MoveNodeRequest.
/// Checks Delete on source namespace and Create on target namespace.
/// </summary>
public class MoveNodePermissionAttribute() : RequiresPermissionAttribute(Permission.Update)
{
    public override IEnumerable<(string Path, Permission Permission)> GetPermissionChecks(
        IMessageDelivery delivery, string hubPath)
    {
        if (delivery.Message is MoveNodeRequest move)
        {
            yield return (GetNamespace(move.SourcePath), Permission.Delete);
            yield return (GetNamespace(move.TargetPath), Permission.Create);
        }
        else
        {
            yield return (hubPath, Permission.Update);
        }
    }

    private static string GetNamespace(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash > 0 ? path[..lastSlash] : path;
    }
}

/// <summary>
/// Response for node move request.
/// </summary>
/// <param name="Node">The moved node at its new path, or null if failed</param>
public record MoveNodeResponse(MeshNode? Node)
{
    /// <summary>
    /// Error message if the move failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Indicates if the move was successful.
    /// </summary>
    public bool Success => Error == null && Node != null;

    /// <summary>
    /// The rejection reason if the move was rejected.
    /// </summary>
    public NodeMoveRejectionReason? RejectionReason { get; init; }

    /// <summary>
    /// Creates a successful move response.
    /// </summary>
    public static MoveNodeResponse Ok(MeshNode node) => new(node);

    /// <summary>
    /// Creates a failed move response with an error message.
    /// </summary>
    public static MoveNodeResponse Fail(string error, NodeMoveRejectionReason reason = NodeMoveRejectionReason.Unknown)
        => new((MeshNode?)null) { Error = error, RejectionReason = reason };
}

/// <summary>
/// Reasons why a node move request can be rejected.
/// </summary>
public enum NodeMoveRejectionReason
{
    /// <summary>
    /// Unknown or unspecified reason.
    /// </summary>
    Unknown,

    /// <summary>
    /// The source node was not found.
    /// </summary>
    SourceNotFound,

    /// <summary>
    /// A node already exists at the target path.
    /// </summary>
    TargetAlreadyExists,

    /// <summary>
    /// Move validation failed.
    /// </summary>
    ValidationFailed
}
