using System.Reflection;
using OpenSmc.Messaging;
using OpenSmc.Scopes;
using OpenSmc.Scopes.Synchronization;

namespace OpenSmc.Application.Scope;

public record SubscribeToEvaluationRequest(string Id,
                                           Func<Task<object>> Expression,
                                           EvaluationSubscriptionOptions Options) : IRequest<ScopeExpressionChangedEvent>
{
    public SubscribeToEvaluationRequest(string Id, Func<Task<object>> Expression, 
        Func<EvaluationSubscriptionOptions, EvaluationSubscriptionOptions> optionFactory)
        : this(Id, Expression, optionFactory(new()))
    {
    }

    public SubscribeToEvaluationRequest(string Id, Func<Task<object>> Expression)
        : this(Id, Expression, new EvaluationSubscriptionOptions())
    {
    }
}

public record UnsubscribeFromEvaluationRequest(string Id);

public record ScopeExpressionChangedEvent(string Id, object Value, ExpressionChangedStatus Status, IReadOnlyCollection<(IMutableScope Scope, PropertyInfo Property)> Dependencies, TimeSpan ExecutionTime, Exception Exception = null);
