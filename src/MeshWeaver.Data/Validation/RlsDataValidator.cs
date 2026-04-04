using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data.Validation;

/// <summary>
/// Data validator that enforces Row-Level Security based on access restrictions.
/// Evaluates global and type-specific access restrictions for data operations.
/// Mirrors the RlsNodeValidator pattern from MeshWeaver.Hosting.Security.
/// </summary>
public class RlsDataValidator : IDataValidator
{
    private readonly IWorkspace _workspace;
    private readonly AccessService _accessService;
    private readonly ILogger<RlsDataValidator> _logger;

    public RlsDataValidator(
        IWorkspace workspace,
        AccessService accessService,
        ILogger<RlsDataValidator> logger)
    {
        _workspace = workspace;
        _accessService = accessService;
        _logger = logger;
    }

    /// <summary>
    /// This validator handles Create, Update, and Delete operations.
    /// Read operations can be handled by separate IDataValidator implementations
    /// or by filtering results at query time.
    /// </summary>
    public IReadOnlyCollection<DataOperation> SupportedOperations =>
        [DataOperation.Create, DataOperation.Update, DataOperation.Delete];

    public async Task<DataValidationResult> ValidateAsync(
        DataValidationContext context,
        CancellationToken ct = default)
    {
        var action = AccessAction.FromOperation(context.Operation);
        var dataContext = _workspace.DataContext;

        // Build access restriction context
        var accessRestrictionContext = new AccessRestrictionContext
        {
            UserContext = context.AccessContext ?? _accessService.Context ?? _accessService.CircuitContext,
            ServiceProvider = _workspace.Hub.ServiceProvider
        };

        // Get type-specific restrictions
        var typeSource = dataContext.GetTypeSource(context.EntityType);
        var typeRestrictions = typeSource?.AccessRestrictions
            ?? System.Collections.Immutable.ImmutableList<AccessRestrictionEntry>.Empty;

        // Combine global restrictions with type-specific (global first)
        var allRestrictions = dataContext.GlobalAccessRestrictions.AddRange(typeRestrictions);

        // Evaluate all restrictions
        foreach (var restriction in allRestrictions)
        {
            try
            {
                var allowed = await restriction.Restriction(action, context.Entity, accessRestrictionContext, ct);

                if (!allowed)
                {
                    var userId = accessRestrictionContext.UserContext?.ObjectId ?? "(anonymous)";
                    var restrictionName = restriction.Name ?? "unnamed";

                    _logger.LogWarning(
                        "RLS: Access denied for user {UserId} - {Operation} on {EntityType} (rule: {RuleName})",
                        userId,
                        context.Operation,
                        context.EntityType.Name,
                        restrictionName);

                    return DataValidationResult.Invalid(
                        $"Access denied: {context.Operation} operation not allowed for {context.EntityType.Name}" +
                        (restriction.Name != null ? $" (rule: {restriction.Name})" : ""),
                        DataValidationRejectionReason.Unauthorized);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "RLS: Access check failed for {Operation} on {EntityType} (rule: {RuleName})",
                    context.Operation,
                    context.EntityType.Name,
                    restriction.Name ?? "unnamed");

                return DataValidationResult.Invalid(
                    $"Access check failed: {ex.Message}",
                    DataValidationRejectionReason.ValidationFailed);
            }
        }

        _logger.LogTrace(
            "RLS: Access granted for user {UserId} - {Operation} on {EntityType}",
            accessRestrictionContext.UserContext?.ObjectId ?? "(anonymous)",
            context.Operation,
            context.EntityType.Name);

        return DataValidationResult.Valid();
    }

    /// <summary>
    /// Checks if a type-level operation is allowed for the current user.
    /// Used for filtering types in type: unified path queries.
    /// </summary>
    /// <param name="type">The type to check access for</param>
    /// <param name="action">The action to check (e.g., "Create")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the action is allowed for the type</returns>
    public async Task<bool> CheckTypeAccessAsync(
        Type type,
        string action,
        CancellationToken ct = default)
    {
        var dataContext = _workspace.DataContext;

        var accessRestrictionContext = new AccessRestrictionContext
        {
            UserContext = _accessService.Context ?? _accessService.CircuitContext,
            ServiceProvider = _workspace.Hub.ServiceProvider
        };

        // Get type-specific restrictions
        var typeSource = dataContext.GetTypeSource(type);
        var typeRestrictions = typeSource?.AccessRestrictions
            ?? System.Collections.Immutable.ImmutableList<AccessRestrictionEntry>.Empty;

        // Combine global restrictions with type-specific
        var allRestrictions = dataContext.GlobalAccessRestrictions.AddRange(typeRestrictions);

        // Evaluate all restrictions with Type as the operation context
        foreach (var restriction in allRestrictions)
        {
            try
            {
                var allowed = await restriction.Restriction(action, type, accessRestrictionContext, ct);
                if (!allowed)
                {
                    _logger.LogWarning(
                        "RLS: Type-level access denied - {Action} on {Type} (rule: {RuleName})",
                        action,
                        type.Name,
                        restriction.Name ?? "unnamed");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "RLS: Type-level access check failed for {Action} on {Type}",
                    action,
                    type.Name);
                return false;
            }
        }

        return true;
    }
}
