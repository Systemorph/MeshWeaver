namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Interface for custom node deletion validation.
/// Implementations can be registered to provide additional validation logic
/// before a node is deleted.
/// </summary>
public interface INodeDeletionValidator
{
    /// <summary>
    /// Validates a node deletion request before the node is deleted.
    /// </summary>
    /// <param name="node">The node that is about to be deleted</param>
    /// <param name="request">The original deletion request</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Validation result with optional error message</returns>
    Task<NodeDeletionValidationResult> ValidateAsync(MeshNode node, DeleteNodeRequest request, CancellationToken ct = default);
}

/// <summary>
/// Result of node deletion validation.
/// </summary>
public record NodeDeletionValidationResult(bool IsValid, string? ErrorMessage = null, NodeDeletionRejectionReason Reason = NodeDeletionRejectionReason.Unknown)
{
    public static NodeDeletionValidationResult Valid() => new(true);

    public static NodeDeletionValidationResult Invalid(string error, NodeDeletionRejectionReason reason = NodeDeletionRejectionReason.ValidationFailed)
        => new(false, error, reason);
}
