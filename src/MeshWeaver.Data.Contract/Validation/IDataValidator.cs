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

/// <summary>
/// Context describing a single data operation to be validated.
/// </summary>
public record DataValidationContext
{
    /// <summary>The kind of operation being validated.</summary>
    public required DataOperation Operation { get; init; }
    /// <summary>The entity being created, updated, deleted or read.</summary>
    public required object Entity { get; init; }
    /// <summary>The current persisted entity, for update/delete operations; null otherwise.</summary>
    public object? ExistingEntity { get; init; }
    /// <summary>The originating request, if available.</summary>
    public object? Request { get; init; }
    /// <summary>The CLR type of the entity.</summary>
    public required Type EntityType { get; init; }
    /// <summary>The access context of the caller, or null for anonymous operations.</summary>
    public AccessContext? AccessContext { get; init; }
    /// <summary>Optional service provider for resolving additional services during validation.</summary>
    public IServiceProvider? ServiceProvider { get; init; }
}

/// <summary>
/// The kind of data operation being performed or validated.
/// </summary>
public enum DataOperation
{
    /// <summary>Reading an entity.</summary>
    Read,
    /// <summary>Creating a new entity.</summary>
    Create,
    /// <summary>Updating an existing entity.</summary>
    Update,
    /// <summary>Deleting an entity.</summary>
    Delete
}
