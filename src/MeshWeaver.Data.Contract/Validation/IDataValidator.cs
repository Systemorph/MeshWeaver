using MeshWeaver.Messaging;

namespace MeshWeaver.Data.Validation;

/// <summary>
/// Unified validator interface for all data operations.
/// </summary>
public interface IDataValidator
{
    /// <summary>
    /// Validates a data operation reactively. Implementations bridge to async I/O at this boundary.
    /// </summary>
    IObservable<DataValidationResult> Validate(DataValidationContext context);

    /// <summary>
    /// Operations this validator handles. Empty collection means all operations.
    /// </summary>
    IReadOnlyCollection<DataOperation> SupportedOperations { get; }
}

public record DataValidationContext
{
    public required DataOperation Operation { get; init; }
    public required object Entity { get; init; }
    public object? ExistingEntity { get; init; }
    public object? Request { get; init; }
    public required Type EntityType { get; init; }
    public AccessContext? AccessContext { get; init; }
    public IServiceProvider? ServiceProvider { get; init; }
}

public enum DataOperation
{
    Read,
    Create,
    Update,
    Delete
}
