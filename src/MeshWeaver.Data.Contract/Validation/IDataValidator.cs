using MeshWeaver.Messaging;

namespace MeshWeaver.Data.Validation;

/// <summary>
/// Unified validator interface for all data operations.
/// Replaces IDataChangeValidator, IDataReadValidator, IDataReadResultValidator.
/// Mirrors the INodeValidator pattern from MeshWeaver.Mesh.
/// </summary>
public interface IDataValidator
{
    /// <summary>
    /// Validates a data operation.
    /// </summary>
    /// <param name="context">Context containing the operation, entity, and optional request</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Validation result</returns>
    Task<DataValidationResult> ValidateAsync(DataValidationContext context, CancellationToken ct = default);

    /// <summary>
    /// Operations this validator handles. Empty collection means all operations.
    /// Use this to create validators that only handle specific operations.
    /// </summary>
    IReadOnlyCollection<DataOperation> SupportedOperations { get; }
}

/// <summary>
/// Context for data validation containing all relevant information.
/// </summary>
public record DataValidationContext
{
    /// <summary>
    /// The operation being validated.
    /// </summary>
    public required DataOperation Operation { get; init; }

    /// <summary>
    /// The entity being operated on.
    /// For Create: the new entity being created.
    /// For Read: the entity being read (or null for collection reads).
    /// For Update: the updated entity (new state).
    /// For Delete: the entity being deleted.
    /// </summary>
    public required object Entity { get; init; }

    /// <summary>
    /// For Update operations, the existing entity before modification.
    /// Null for other operations.
    /// </summary>
    public object? ExistingEntity { get; init; }

    /// <summary>
    /// The original request object (DataChangeRequest for write operations).
    /// Null for Read operations.
    /// </summary>
    public object? Request { get; init; }

    /// <summary>
    /// The type of entity being operated on.
    /// </summary>
    public required Type EntityType { get; init; }

    /// <summary>
    /// The current user's access context.
    /// May be null for anonymous operations.
    /// </summary>
    public AccessContext? AccessContext { get; init; }

    /// <summary>
    /// Optional service provider for resolving additional services.
    /// </summary>
    public IServiceProvider? ServiceProvider { get; init; }
}

/// <summary>
/// Data operations that can be validated.
/// </summary>
public enum DataOperation
{
    /// <summary>
    /// Reading data from the workspace.
    /// </summary>
    Read,

    /// <summary>
    /// Creating new data entities.
    /// </summary>
    Create,

    /// <summary>
    /// Updating existing data entities.
    /// </summary>
    Update,

    /// <summary>
    /// Deleting data entities.
    /// </summary>
    Delete
}
