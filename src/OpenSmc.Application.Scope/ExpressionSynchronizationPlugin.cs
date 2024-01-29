using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using OpenSmc.Messaging;
using OpenSmc.Queues;
using OpenSmc.Scopes;
using OpenSmc.Scopes.Proxy;
using OpenSmc.Scopes.Synchronization;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Application.Scope;

public record ExpressionSynchronizationHubState(IInternalMutableScope ApplicationScope, ScopeSynchronizationsCache ScopeSynchronizationsCache)
{
    public ImmutableDictionary<string, ExpressionRegistryItem> RegisteredExpressions { get; init; } = ImmutableDictionary<string, ExpressionRegistryItem>.Empty;
}

public class ExpressionSynchronizationPlugin(IMessageHub hub)
    : MessageHubPlugin<ExpressionSynchronizationPlugin, ExpressionSynchronizationHubState>(hub),
        IMessageHandler<SubscribeToEvaluationRequest>,
        IMessageHandler<UnsubscribeFromEvaluationRequest>,
        IMessageHandler<ScopePropertyChangedEvent>
{
    [Inject] private IApplicationScope applicationScope;

    public override ExpressionSynchronizationHubState StartupState()
    {
        // ReSharper disable once SuspiciousTypeConversion.Global
        return new((IInternalMutableScope)applicationScope, new(Hub));
    }

    IMessageDelivery IMessageHandler<ScopePropertyChangedEvent>.HandleMessage(IMessageDelivery<ScopePropertyChangedEvent> delivery)
    {
        InvalidateExpressions(delivery.Message.Scope, delivery.Message.Property);
        return delivery.Processed();
    }
    public IMessageDelivery HandleMessage(IMessageDelivery<UnsubscribeFromEvaluationRequest> request)
    {
        var id = request.Message.Id;

        var ret = State.RegisteredExpressions.TryGetValue(id, out var item);
        if (ret)
        {
            UpdateState(s => s with { RegisteredExpressions = s.RegisteredExpressions.Remove(id) });
            item.Dispose();
        }
        return request.Processed();
    }

    public IMessageDelivery HandleMessage(IMessageDelivery<SubscribeToEvaluationRequest> request)
    {
        var id = request.Message.Id;
        var expression = request.Message.Expression;
        var options = request.Message.Options;
        var item = new ExpressionRegistryItem(id, expression, options, request);
        UpdateState(s => s with { RegisteredExpressions = s.RegisteredExpressions.SetItem(id, item) });
        return EnqueueExpression(item);
    }

    private IMessageDelivery EnqueueExpression(ExpressionRegistryItem item)
    {
        var request = item.Request;

        var scopeExpressionChangedEvent = new ScopeExpressionChangedEvent(item.Id, null, ExpressionChangedStatus.Evaluating, Array.Empty<(IMutableScope Scope, PropertyInfo Property)>(), TimeSpan.Zero);
        Hub.Post(scopeExpressionChangedEvent, o => o.ResponseFor(item.Request));

        var enqueueRequest = new EnqueueRequest(async () =>
                                                {
                                                    // see if anyone unregistered...
                                                    if (!State.RegisteredExpressions.TryGetValue(item.Id, out item))
                                                        return;

                                                    var sw = new Stopwatch();
                                                    sw.Start();
                                                    try
                                                    {
                                                        var evaluation = await State.ApplicationScope.EvaluateWithDependenciesAsync(item.GenerationFunction);
                                                        item.Dependencies = evaluation.Dependencies.GroupBy(x => x.Scope).ToDictionary(x => (object)x.Key, x => x.GroupBy(y => y.Property.Name).ToDictionary(y => y.Key, y => y.ToArray()));
                                                        item.Value = evaluation.Result;
                                                        if (item.Dependencies.Any())
                                                        {
                                                            var scopes = item.Dependencies.Values.SelectMany(x => x.Values.SelectMany(y => y.Select(z => z.Scope))).Distinct().OfType<IInternalMutableScope>().ToArray();
                                                            State.ScopeSynchronizationsCache.Synchronize(scopes, item.Id);
                                                            item.DisposeActions.Add(() => State.ScopeSynchronizationsCache.StopSynchronization(scopes, item.Id));
                                                        }

                                                        var expressionChangedEvent = new ScopeExpressionChangedEvent(item.Id, evaluation.Result, evaluation.Status, evaluation.Dependencies, sw.Elapsed);

                                                        Hub.Post(expressionChangedEvent, o => o.WithTarget(MessageTargets.Subscribers));
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        var expressionChangedEvent = new ScopeExpressionChangedEvent(item.Id, ex, ExpressionChangedStatus.Error, null, sw.Elapsed, ex);
                                                        Hub.Post(expressionChangedEvent, o => o.ResponseFor(request));
                                                    }
                                                }, item.Id);

        Hub.Schedule(enqueueRequest.Action);

        return request.Processed();
    }


    private void InvalidateExpressions(object scope, string propertyName)
    {
        foreach (var expressionRegistryItem in State.RegisteredExpressions.Values.Where(x => x.Options.Mode == EvaluationRefreshMode.Recompute))
        {
            if ((expressionRegistryItem.Dependencies?.TryGetValue(scope, out var inner) ?? false) && inner.ContainsKey(propertyName))
            {
                Hub.Schedule(() => Task.FromResult(EnqueueExpression(expressionRegistryItem)));
            }
        }
    }
}

public record ExpressionRegistryItem(string Id, Func<Task<object>> GenerationFunction, EvaluationSubscriptionOptions Options, IMessageDelivery Request):IDisposable
{
    public Dictionary<object, Dictionary<string, (IMutableScope Scope, PropertyInfo Property)[]>> Dependencies { get; set; }
    public object Value { get; set; }
    public readonly List<Action> DisposeActions = new();


    public void Dispose()
    {
        foreach (var disposable in DisposeActions)
        {
            disposable();
        }
    }
}