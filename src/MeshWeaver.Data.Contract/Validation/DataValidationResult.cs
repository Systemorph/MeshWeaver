namespace MeshWeaver.Data.Validation;

/// <summary>
/// Result of data validation.
/// </summary>
public record DataValidationResult(bool IsValid, string? ErrorMessage = null, DataValidationRejectionReason Reason = DataValidationRejectionReason.Unknown)
{
    /// <summary>Creates a successful validation result.</summary>
    /// <returns>A valid result.</returns>
    public static DataValidationResult Valid() => new(true);

    /// <summary>Creates a failed validation result with the given error and reason.</summary>
    /// <param name="error">The error message describing why validation failed.</param>
    /// <param name="reason">The categorized rejection reason.</param>
    /// <returns>An invalid result.</returns>
    public static DataValidationResult Invalid(string error, DataValidationRejectionReason reason = DataValidationRejectionReason.ValidationFailed)
        => new(false, error, reason);
}

/// <summary>
/// Reasons why a data operation might be rejected.
/// </summary>
public enum DataValidationRejectionReason
{
    /// <summary>The rejection reason is unknown or unspecified.</summary>
    Unknown,
    /// <summary>The entity failed a validation rule.</summary>
    ValidationFailed,
    /// <summary>The caller is not authorized to perform the operation.</summary>
    Unauthorized,
    /// <summary>The target entity was not found.</summary>
    EntityNotFound,
    /// <summary>The operation conflicted with a concurrent change.</summary>
    ConcurrencyConflict,
    /// <summary>The entity references another entity that is invalid or missing.</summary>
    InvalidReference,
    /// <summary>The entity is hidden from the caller.</summary>
    EntityHidden
}
