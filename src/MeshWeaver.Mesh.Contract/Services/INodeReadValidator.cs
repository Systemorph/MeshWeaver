namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Interface for custom node read validation.
/// Implementations can be registered to provide additional validation logic
/// before a node is returned from a read operation.
/// </summary>
public interface INodeReadValidator
{
    /// <summary>
    /// Validates a node read request before the node is returned.
    /// </summary>
    /// <param name="node">The node being read</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Validation result with optional error message</returns>
    Task<NodeReadValidationResult> ValidateAsync(MeshNode node, CancellationToken ct = default);
}

/// <summary>
/// Result of node read validation.
/// </summary>
public record NodeReadValidationResult(bool IsValid, string? ErrorMessage = null, NodeReadRejectionReason Reason = NodeReadRejectionReason.Unknown)
{
    public static NodeReadValidationResult Valid() => new(true);

    public static NodeReadValidationResult Invalid(string error, NodeReadRejectionReason reason = NodeReadRejectionReason.ValidationFailed)
        => new(false, error, reason);
}

/// <summary>
/// Reasons why a node read might be rejected.
/// </summary>
public enum NodeReadRejectionReason
{
    Unknown,
    ValidationFailed,
    Unauthorized,
    NodeHidden
}
