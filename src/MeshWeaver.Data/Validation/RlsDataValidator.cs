using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data.Validation;

/// <summary>
/// Data validator that enforces Row-Level Security based on access restrictions.
/// Reactive end-to-end: the validator surface returns <see cref="IObservable{T}"/>,
/// each restriction call is wrapped with <see cref="Observable.FromAsync"/> at the
/// inner edge, and the chain composes via recursive <c>SelectMany</c> with no <c>await</c>.
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

    public IObservable<DataValidationResult> Validate(DataValidationContext context)
    {
        var action = AccessAction.FromOperation(context.Operation);
        var dataContext = _workspace.DataContext;

        var accessRestrictionContext = new AccessRestrictionContext
        {
            UserContext = context.AccessContext ?? _accessService.Context ?? _accessService.CircuitContext,
            ServiceProvider = _workspace.Hub.ServiceProvider
        };

        var typeSource = dataContext.GetTypeSource(context.EntityType);
        var typeRestrictions = typeSource?.AccessRestrictions
            ?? ImmutableList<AccessRestrictionEntry>.Empty;

        var allRestrictions = dataContext.GlobalAccessRestrictions.AddRange(typeRestrictions);

        return EvaluateRestrictions(allRestrictions, action, context, accessRestrictionContext);
    }

    /// <summary>
    /// Recursive observable composition over a list of access restrictions.
    /// Stops at the first denial; otherwise advances to the next restriction.
    /// </summary>
    private IObservable<DataValidationResult> EvaluateRestrictions(
        ImmutableList<AccessRestrictionEntry> restrictions,
        string action,
        DataValidationContext context,
        AccessRestrictionContext accessCtx)
    {
        if (restrictions.Count == 0)
        {
            _logger.LogTrace(
                "RLS: Access granted for user {UserId} - {Operation} on {EntityType}",
                accessCtx.UserContext?.ObjectId ?? "(anonymous)",
                context.Operation,
                context.EntityType.Name);
            return Observable.Return(DataValidationResult.Valid());
        }

        var first = restrictions[0];
        var rest = restrictions.RemoveAt(0);

        return first.Restriction(action, context.Entity, accessCtx)
            .Catch<bool, Exception>(ex =>
            {
                _logger.LogError(ex,
                    "RLS: Access check failed for {Operation} on {EntityType} (rule: {RuleName})",
                    context.Operation, context.EntityType.Name, first.Name ?? "unnamed");
                return Observable.Return(false);
            })
            .SelectMany(allowed =>
            {
                if (!allowed)
                {
                    _logger.LogWarning(
                        "RLS: Access denied for user {UserId} - {Operation} on {EntityType} (rule: {RuleName})",
                        accessCtx.UserContext?.ObjectId ?? "(anonymous)",
                        context.Operation,
                        context.EntityType.Name,
                        first.Name ?? "unnamed");

                    return Observable.Return(DataValidationResult.Invalid(
                        $"Access denied: {context.Operation} operation not allowed for {context.EntityType.Name}" +
                        (first.Name != null ? $" (rule: {first.Name})" : ""),
                        DataValidationRejectionReason.Unauthorized));
                }
                return EvaluateRestrictions(rest, action, context, accessCtx);
            });
    }

    /// <summary>
    /// Reactive type-level access check. Used for filtering types in <c>type:</c> unified-path queries.
    /// </summary>
    public IObservable<bool> CheckTypeAccess(Type type, string action)
    {
        var dataContext = _workspace.DataContext;

        var accessRestrictionContext = new AccessRestrictionContext
        {
            UserContext = _accessService.Context ?? _accessService.CircuitContext,
            ServiceProvider = _workspace.Hub.ServiceProvider
        };

        var typeSource = dataContext.GetTypeSource(type);
        var typeRestrictions = typeSource?.AccessRestrictions
            ?? ImmutableList<AccessRestrictionEntry>.Empty;

        var allRestrictions = dataContext.GlobalAccessRestrictions.AddRange(typeRestrictions);
        return EvaluateTypeRestrictions(allRestrictions, action, type, accessRestrictionContext);
    }

    private IObservable<bool> EvaluateTypeRestrictions(
        ImmutableList<AccessRestrictionEntry> restrictions,
        string action,
        Type type,
        AccessRestrictionContext accessCtx)
    {
        if (restrictions.Count == 0)
            return Observable.Return(true);

        var first = restrictions[0];
        var rest = restrictions.RemoveAt(0);

        return first.Restriction(action, type, accessCtx)
            .Catch<bool, Exception>(ex =>
            {
                _logger.LogError(ex,
                    "RLS: Type-level access check failed for {Action} on {Type}",
                    action, type.Name);
                return Observable.Return(false);
            })
            .SelectMany(allowed =>
            {
                if (!allowed)
                {
                    _logger.LogWarning(
                        "RLS: Type-level access denied - {Action} on {Type} (rule: {RuleName})",
                        action, type.Name, first.Name ?? "unnamed");
                    return Observable.Return(false);
                }
                return EvaluateTypeRestrictions(rest, action, type, accessCtx);
            });
    }
}
