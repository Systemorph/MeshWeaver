namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Interface for custom node creation validation.
/// Implementations can be registered to provide additional validation logic
/// that runs after the node is created in Transient state.
/// If validation fails, the transient node is deleted.
/// </summary>
public interface INodeCreationValidator
{
    /// <summary>
    /// Validates a node creation request after the node has been created in Transient state.
    /// </summary>
    /// <param name="node">The transient node that was created</param>
    /// <param name="request">The original creation request</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Validation result with optional error message</returns>
    Task<NodeValidationResult> ValidateAsync(MeshNode node, CreateNodeRequest request, CancellationToken ct = default);
}

/// <summary>
/// Result of node creation validation.
/// </summary>
public record NodeValidationResult(bool IsValid, string? ErrorMessage = null, NodeCreationRejectionReason Reason = NodeCreationRejectionReason.Unknown)
{
    public static NodeValidationResult Valid() => new(true);

    public static NodeValidationResult Invalid(string error, NodeCreationRejectionReason reason = NodeCreationRejectionReason.ValidationFailed)
        => new(false, error, reason);
}
