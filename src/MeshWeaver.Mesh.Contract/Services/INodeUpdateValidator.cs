namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Interface for custom node update validation.
/// Implementations can be registered to provide additional validation logic
/// before a node is updated.
/// </summary>
public interface INodeUpdateValidator
{
    /// <summary>
    /// Validates a node update request before the node is updated.
    /// </summary>
    /// <param name="existingNode">The current node before update</param>
    /// <param name="updatedNode">The node with proposed changes</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Validation result with optional error message</returns>
    Task<NodeUpdateValidationResult> ValidateAsync(MeshNode existingNode, MeshNode updatedNode, CancellationToken ct = default);
}

/// <summary>
/// Result of node update validation.
/// </summary>
public record NodeUpdateValidationResult(bool IsValid, string? ErrorMessage = null, NodeUpdateRejectionReason Reason = NodeUpdateRejectionReason.Unknown)
{
    public static NodeUpdateValidationResult Valid() => new(true);

    public static NodeUpdateValidationResult Invalid(string error, NodeUpdateRejectionReason reason = NodeUpdateRejectionReason.ValidationFailed)
        => new(false, error, reason);
}

/// <summary>
/// Reasons why a node update might be rejected.
/// </summary>
public enum NodeUpdateRejectionReason
{
    Unknown,
    NodeNotFound,
    ValidationFailed,
    Unauthorized,
    InvalidState,
    ConcurrencyConflict
}
