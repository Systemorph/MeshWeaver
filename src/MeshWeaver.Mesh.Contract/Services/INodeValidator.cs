using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Unified validator interface for all node operations.
/// Replaces INodeReadValidator, INodeCreationValidator, INodeUpdateValidator, INodeDeletionValidator.
/// </summary>
public interface INodeValidator
{
    /// <summary>
    /// Validates a node operation.
    /// </summary>
    /// <param name="context">Context containing the operation, node(s), and optional request</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Validation result</returns>
    Task<NodeValidationResult> ValidateAsync(NodeValidationContext context, CancellationToken ct = default);

    /// <summary>
    /// Operations this validator handles. Empty collection means all operations.
    /// Use this to create validators that only handle specific operations.
    /// </summary>
    IReadOnlyCollection<NodeOperation> SupportedOperations { get; }
}

/// <summary>
/// Context for node validation containing all relevant information.
/// </summary>
public record NodeValidationContext
{
    /// <summary>
    /// The operation being validated.
    /// </summary>
    public required NodeOperation Operation { get; init; }

    /// <summary>
    /// The node being operated on.
    /// For Create: the new node being created.
    /// For Read: the node being read.
    /// For Update: the updated node (new state).
    /// For Delete: the node being deleted.
    /// </summary>
    public required MeshNode Node { get; init; }

    /// <summary>
    /// For Update operations, the existing node before modification.
    /// Null for other operations.
    /// </summary>
    public MeshNode? ExistingNode { get; init; }

    /// <summary>
    /// The original request object (CreateNodeRequest, DeleteNodeRequest, UpdateNodeRequest).
    /// Null for Read operations.
    /// </summary>
    public object? Request { get; init; }

    /// <summary>
    /// The current user's access context.
    /// May be null for anonymous operations.
    /// </summary>
    public AccessContext? AccessContext { get; init; }
}

/// <summary>
/// Unified validation result for all node operations.
/// </summary>
public record NodeValidationResult(bool IsValid, string? ErrorMessage = null, NodeRejectionReason Reason = NodeRejectionReason.Unknown)
{
    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static NodeValidationResult Valid() => new(true);

    /// <summary>
    /// Creates a failed validation result with an error message.
    /// </summary>
    public static NodeValidationResult Invalid(string error, NodeRejectionReason reason = NodeRejectionReason.ValidationFailed)
        => new(false, error, reason);

    /// <summary>
    /// Creates an unauthorized validation result.
    /// </summary>
    public static NodeValidationResult Unauthorized(string? message = null)
        => new(false, message ?? "Access denied", NodeRejectionReason.Unauthorized);

    /// <summary>
    /// Creates a node not found validation result.
    /// </summary>
    public static NodeValidationResult NotFound(string? path = null)
        => new(false, path != null ? $"Node not found at path: {path}" : "Node not found", NodeRejectionReason.NodeNotFound);
}

/// <summary>
/// Per-node-type access rule that replaces the standard RLS permission check.
/// Register via DI to provide custom access logic for specific node types.
/// When RlsNodeValidator encounters a node whose type has a registered access rule,
/// it delegates to the rule instead of checking AccessAssignment permissions.
/// </summary>
public interface INodeTypeAccessRule
{
    /// <summary>
    /// The node type this rule applies to (e.g. "VUser").
    /// </summary>
    string NodeType { get; }

    /// <summary>
    /// Operations this rule handles. Must be a subset of the RLS validator's operations.
    /// </summary>
    IReadOnlyCollection<NodeOperation> SupportedOperations { get; }

    /// <summary>
    /// Checks whether the given user/context has access for the operation.
    /// Returns true to allow, false to deny.
    /// </summary>
    Task<bool> HasAccessAsync(NodeValidationContext context, string? userId, CancellationToken ct = default);
}

/// <summary>
/// Unified rejection reasons for all node operations.
/// </summary>
public enum NodeRejectionReason
{
    /// <summary>
    /// Unknown or unspecified reason.
    /// </summary>
    Unknown,

    /// <summary>
    /// General validation failure.
    /// </summary>
    ValidationFailed,

    /// <summary>
    /// User is not authorized to perform the operation.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// The requested node was not found.
    /// </summary>
    NodeNotFound,

    /// <summary>
    /// A node already exists at the specified path.
    /// </summary>
    NodeAlreadyExists,

    /// <summary>
    /// The specified NodeType is invalid or not registered.
    /// </summary>
    InvalidNodeType,

    /// <summary>
    /// The node path is invalid.
    /// </summary>
    InvalidPath,

    /// <summary>
    /// Cannot delete a node that has children (when recursive=false).
    /// </summary>
    HasChildren,

    /// <summary>
    /// Concurrent modification conflict.
    /// </summary>
    ConcurrencyConflict,

    /// <summary>
    /// The node is hidden from the user.
    /// </summary>
    NodeHidden
}
