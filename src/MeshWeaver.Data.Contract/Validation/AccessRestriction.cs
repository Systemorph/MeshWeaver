using MeshWeaver.Messaging;

namespace MeshWeaver.Data.Validation;

/// <summary>
/// Standard access actions for data operations.
/// </summary>
public static class AccessAction
{
    public const string Create = "Create";
    public const string Read = "Read";
    public const string Update = "Update";
    public const string Delete = "Delete";

    /// <summary>
    /// Converts a DataOperation enum to its string action name.
    /// </summary>
    public static string FromOperation(DataOperation operation) => operation switch
    {
        DataOperation.Create => Create,
        DataOperation.Read => Read,
        DataOperation.Update => Update,
        DataOperation.Delete => Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };
}

/// <summary>
/// Delegate for evaluating access restrictions.
/// </summary>
/// <param name="action">The action being performed (Create, Read, Update, Delete)</param>
/// <param name="operationContext">
/// Context object that varies by check type:
/// - Type: the Type being checked (for type-level access)
/// - Instance: the entity instance (for row-level access)
/// - DataChangeRequest: the full request (for batch validation)
/// </param>
/// <param name="context">Access restriction context with user info</param>
/// <returns>True if access is allowed, false otherwise</returns>
public delegate Task<bool> AccessRestrictionDelegate(
    string action,
    object operationContext,
    AccessRestrictionContext context);

/// <summary>
/// Context passed to access restriction lambdas.
/// Contains the current user context and operation details.
/// </summary>
public record AccessRestrictionContext
{
    /// <summary>
    /// The current user's access context (from AccessService).
    /// May be null for anonymous operations.
    /// </summary>
    public AccessContext? UserContext { get; init; }

    /// <summary>
    /// Optional service provider for resolving additional services.
    /// </summary>
    public IServiceProvider? ServiceProvider { get; init; }
}

/// <summary>
/// Entry representing a configured access restriction.
/// </summary>
/// <param name="Restriction">The restriction delegate to evaluate</param>
/// <param name="Name">Optional name for logging/debugging</param>
public record AccessRestrictionEntry(
    AccessRestrictionDelegate Restriction,
    string? Name = null);
