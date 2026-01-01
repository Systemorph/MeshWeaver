namespace MeshWeaver.Data.Validation;

/// <summary>
/// Interface for custom data change validation.
/// Implementations can be registered to provide validation logic
/// before data changes (creates, updates, deletes) are applied via DataChangeRequest.
/// </summary>
[Obsolete("Use IDataValidator instead for unified validation. This interface will be removed in a future version.")]
public interface IDataChangeValidator
{
    /// <summary>
    /// Validates a data change request before it's applied.
    /// </summary>
    /// <param name="request">The data change request containing creations, updates, and deletions</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Validation result with optional error message</returns>
    Task<DataValidationResult> ValidateAsync(DataChangeRequest request, CancellationToken ct = default);
}
