namespace MeshWeaver.Data.Validation;

/// <summary>
/// Interface for custom data read validation.
/// Implementations can be registered to provide additional validation logic
/// before data is returned from GetDataRequest or SubscribeRequest.
/// </summary>
[Obsolete("Use IDataValidator instead for unified validation. This interface will be removed in a future version.")]
public interface IDataReadValidator
{
    /// <summary>
    /// Validates a data read request before the data is returned.
    /// </summary>
    /// <param name="reference">The workspace reference being read</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Validation result with optional error message</returns>
    Task<DataValidationResult> ValidateAsync(WorkspaceReference reference, CancellationToken ct = default);
}

/// <summary>
/// Interface for custom data read validation with access to the data being returned.
/// Called after data is retrieved but before it's returned to the caller.
/// </summary>
[Obsolete("Use IDataValidator instead for unified validation. This interface will be removed in a future version.")]
public interface IDataReadResultValidator
{
    /// <summary>
    /// Validates the data before it's returned.
    /// </summary>
    /// <param name="reference">The workspace reference that was read</param>
    /// <param name="data">The data being returned</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Validation result with optional error message</returns>
    Task<DataValidationResult> ValidateAsync(WorkspaceReference reference, object? data, CancellationToken ct = default);
}
