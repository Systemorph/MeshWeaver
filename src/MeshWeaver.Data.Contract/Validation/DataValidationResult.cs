namespace MeshWeaver.Data.Validation;

/// <summary>
/// Result of data validation.
/// </summary>
public record DataValidationResult(bool IsValid, string? ErrorMessage = null, DataValidationRejectionReason Reason = DataValidationRejectionReason.Unknown)
{
    public static DataValidationResult Valid() => new(true);

    public static DataValidationResult Invalid(string error, DataValidationRejectionReason reason = DataValidationRejectionReason.ValidationFailed)
        => new(false, error, reason);
}

/// <summary>
/// Reasons why a data operation might be rejected.
/// </summary>
public enum DataValidationRejectionReason
{
    Unknown,
    ValidationFailed,
    Unauthorized,
    EntityNotFound,
    ConcurrencyConflict,
    InvalidReference,
    EntityHidden
}
