using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OpenSmc.Collections;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;
using OpenSmc.Reflection;
using OpenSmc.Scopes;
using OpenSmc.Scopes.Proxy;
using OpenSmc.Scopes.Synchronization;
using OpenSmc.Serialization;
using OpenSmc.ServiceProvider;
using OpenSmc.ShortGuid;

namespace OpenSmc.Application.Scope;

public class ApplicationScopePlugin : MessageHubPlugin<ApplicationScopeState>,
                                        IMessageHandler<SubscribeScopeRequest>,
                                        IMessageHandler<UnsubscribeScopeRequest>,
                                        IMessageHandler<DisposeScopeRequest>,
                                        IMessageHandler<ScopePropertyChanged>,
                                        IMessageHandler<ScopePropertyChangedEvent>,
                                        IMessageHandler<GetRequest<IApplicationScope>>,

IMessageHandler<SubscribeToEvaluationRequest>,
    IMessageHandler<UnsubscribeFromEvaluationRequest>

{
    [Inject] private ILogger<ApplicationScopePlugin> logger;
    private readonly IApplicationScope applicationScope;
    private readonly IScopeRegistry scopeRegistry;
    private readonly ISerializationService serializationService;
    public ApplicationScopePlugin(IMessageHub hub) : base(hub)
    {
        applicationScope = hub.ServiceProvider.GetRequiredService<IApplicationScope>();
        serializationService = hub.ServiceProvider.GetRequiredService<ISerializationService>();
        // ReSharper disable once SuspiciousTypeConversion.Global
        scopeRegistry = ((IInternalMutableScope)applicationScope).GetScopeRegistry();

    }


    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
        InitializeState(new());
        scopeRegistry.InstanceRegistered += (_, scope) => TrackPropertyChanged(scope);
        foreach (var scope in scopeRegistry.Scopes)
            TrackPropertyChanged(scope);

    }

    IMessageDelivery IMessageHandler<SubscribeScopeRequest>.HandleMessage(IMessageDelivery<SubscribeScopeRequest> request)
    {
        var subscribeScopeRequest = request.Message;
        var address = subscribeScopeRequest.Address;
        if (address != null && !address.Equals(Hub.Address))
        {
            if(State.SynchronizedById.TryGetValue((address, subscribeScopeRequest.Id), out var s) || State.SynchronizedByTypeAndIdentity.TryGetValue((address, subscribeScopeRequest.ScopeType, subscribeScopeRequest.Identity), out  s))
            {
                logger.LogDebug($"Subscribing to scope {subscribeScopeRequest.Id} on address{address}");
                Hub.Post(s, o => o.ResponseFor(request));
                return request.Processed();
            }

            Hub.RegisterCallback(Hub.Post(subscribeScopeRequest, o => o.WithTarget(address)), response => SubscribeSynchronizedScope(request, response.Message));
            return request.Forwarded();
        }

        var scope = subscribeScopeRequest.Scope ??
                    (
                        !string.IsNullOrEmpty(subscribeScopeRequest.Id)
                            ? scopeRegistry.GetScope(subscribeScopeRequest.Id.AsGuid())
                            : subscribeScopeRequest.ScopeType != null
                                ? GetScopeMethod.MakeGenericMethod(Type.GetType(subscribeScopeRequest.ScopeType)).InvokeAsFunction(this, request)
                                : null
                    );
        return SubscribeScope(request, scope);
    }

    private IMessageDelivery SubscribeSynchronizedScope(IMessageDelivery<SubscribeScopeRequest> request, object scope)
    {
        if (scope == null)
            return request.NotFound();
        var address = request.Sender;
        SubscribeScope(request, scope);
        var serialized = serializationService.Serialize(scope);
        var dictionary = JsonConvert.DeserializeObject<ImmutableDictionary<string, object>>(serialized.Content);
        if (dictionary.TryGetValue("$scopeId", out var scopeId))
            UpdateState(s => s with { SynchronizedById = s.SynchronizedById.SetItem((address, scopeId.ToString()), dictionary) });
        if (dictionary.TryGetValue("$type", out var scopeType))
            UpdateState(s => s with { SynchronizedByTypeAndIdentity = s.SynchronizedByTypeAndIdentity.SetItem((address, scopeType.ToString(), dictionary.TryGetValue("identity", out var identity) ? identity : null), dictionary) });
        return request.Processed();
    }

    private IMessageDelivery SubscribeScope(IMessageDelivery<SubscribeScopeRequest> request, object scope)
    {
        logger.LogDebug("Subscribing to scope {scope}", (scope as IScope)?.GetGuid());

        Hub.Post(scope, o => o.ResponseFor(request));
        var address = request.Sender;
        UpdateState(s => s with {SubscribedScopes = s.SubscribedScopes.SetItem(scope, (s.SubscribedScopes.TryGetValue(scope, out var list) ? list : ImmutableHashSet<object>.Empty).Add(address))});
        return request.Processed();
    }

    private static readonly MethodInfo GetScopeMethod = ReflectionHelper.GetMethodGeneric<ApplicationScopePlugin>(x => x.GetScope<object>(null));


    // ReSharper disable once UnusedMethodReturnValue.Local
    private object GetScope<TScope>(IMessageDelivery<SubscribeScopeRequest> request)
    {
        var scope = applicationScope.GetScope<TScope>(request.Message.Identity);
        return scope;
    }

    public CreatableObjectStore<(Type Type, string Property), Action<object, object>> PropertySetters { get; set; } = new(CreatePropertySetter);



    IMessageDelivery IMessageHandler<UnsubscribeScopeRequest>.HandleMessage(IMessageDelivery<UnsubscribeScopeRequest> request)
    {
        logger.LogDebug("Subscribing from scope {id}", request.Message.Scope.GetGuid());
        var scope = request.Message.Scope;
        return Unsubscribe(request, scope);

    }

    IMessageDelivery IMessageHandler<ScopePropertyChangedEvent>.HandleMessage(IMessageDelivery<ScopePropertyChangedEvent> delivery)
    {
        var action = delivery.Message;
        if (action.Status != ScopeChangedStatus.Requested)
            return delivery;

        var e = delivery.Message;
        var scope = scopeRegistry.GetScope(e.ScopeId);
        e = e with { Scope = scope };
        OnInPropertyChanged(scope, e);
        return delivery.Processed();

    }

    IMessageDelivery IMessageHandler<ScopePropertyChanged>.HandleMessage(IMessageDelivery<ScopePropertyChanged> request)
    {
        var action = request.Message;
        if (action.Status != PropertyChangeStatus.Requested)
            return request;

        var scopeId = action.ScopeId.AsGuid();
        var scope = (IMutableScope)scopeRegistry.GetScope(scopeId);
        var propertyInfo = ScopeUtils.GetScopePropertyType(scope, action.ScopeId.AsGuid(), action.Property);
        if (propertyInfo == null)
        {
            logger.LogDebug("Property {property} not found", action.Property);
            Hub.Post(action with { Status = PropertyChangeStatus.NotFound });
            return request.Failed($"Property not found: {action.Property}");
        }

        var reader = new StringReader(action.Value.Content);
        var value = ((SerializationService)serializationService).Serializer.Deserialize(reader, propertyInfo.PropertyType);
        var e = new ScopePropertyChangedEvent(scope, scopeId, propertyInfo.Name, value);

        OnInPropertyChanged(scope, e);
        return request.Processed();
    }

    private void OnInPropertyChanged(object scope, ScopePropertyChangedEvent scopePropertyChangedEvent)
    {
        if (scope == null)
        {
            logger.LogDebug("Scope {id} not found", scopePropertyChangedEvent.ScopeId);
            Hub.Post(scopePropertyChangedEvent with { Status = ScopeChangedStatus.NotFound });
        }
        else
        {
            // TODO SMCv2: Review commented (2023/12/19, Alexander Yolokhov)
            // this will also lock the property change queue, as it is configured to be with debounce.
            //stateManger.Hub.Post(new EnqueueRequest(() =>
            //                                        {
            try
            {
                PropertySetters.GetInstance((scope.GetType(), scopePropertyChangedEvent.Property)).Invoke(scope, scopePropertyChangedEvent.Value);
            }
            catch (Exception exception)
            {
                logger.LogDebug("Exception setting property: {exception}", exception);
                OnScopePropertyChanged(scope, scopePropertyChangedEvent with { Status = ScopeChangedStatus.Exception, ErrorMessage = exception.ToString() });
            }

            //    return Task.CompletedTask;
            //}, $"{scopePropertyChangedEvent.ScopeId}_{scopePropertyChangedEvent.Property}"), o => o.WithTarget(propertyChangedQueueHubAddress));
        }
    }

    private void OnScopePropertyChanged(object sender, ScopePropertyChangedEvent e)
    {
        if (State.SubscribedScopes.TryGetValue(sender, out var addresses))
            foreach (var address in addresses)
            {
                logger.LogDebug("Sending scope property change to address {address}", address);
                Hub.Post(e, o => o.WithTarget(address));
            }

        InvalidateExpressions(e.Scope, e.Property);

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
        logger.LogDebug("Subscribing to expression: {expression}", request.Message);
        UpdateState(s => s with { RegisteredExpressions = s.RegisteredExpressions.SetItem(id, item) });
        return EnqueueExpression(item);
    }


    private void InvalidateExpressions(object scope, string propertyName)
    {
        foreach (var expressionRegistryItem in State.RegisteredExpressions.Values.Where(x => x.Options.Mode == EvaluationRefreshMode.Recompute))
        {
            if ((expressionRegistryItem.Dependencies?.TryGetValue(scope, out var inner) ?? false) && inner.ContainsKey(propertyName))
            {
                Hub.Schedule(_ => Task.FromResult(EnqueueExpression(expressionRegistryItem)));
            }
        }
    }
    private IMessageDelivery EnqueueExpression(ExpressionRegistryItem item)
    {
        var request = item.Request;

        var scopeExpressionChangedEvent = new ScopeExpressionChangedEvent(item.Id, null, ExpressionChangedStatus.Evaluating, Array.Empty<(IMutableScope Scope, PropertyInfo Property)>(), TimeSpan.Zero);
        Hub.Post(scopeExpressionChangedEvent, o => o.ResponseFor(item.Request));

        logger.LogDebug("Enqueuing evaluation expression: {expression}", item);
        var enqueueRequest = new EnqueueRequest(async _ =>
        {
            logger.LogDebug("Executing queue for item: {expression}", item);
            // see if anyone unregistered...
            if (!State.RegisteredExpressions.TryGetValue(item.Id, out item))
                return;

            var sw = new Stopwatch();
            sw.Start();
            try
            {
                // ReSharper disable once SuspiciousTypeConversion.Global
                var evaluation = await ((IInternalMutableScope)applicationScope).EvaluateWithDependenciesAsync(item.GenerationFunction);
                item.Dependencies = evaluation.Dependencies.GroupBy(x => x.Scope).ToDictionary(x => (object)x.Key, x => x.GroupBy(y => y.Property.Name).ToDictionary(y => y.Key, y => y.ToArray()));
                item.Value = evaluation.Result;
                if (item.Dependencies.Any())
                {
                    var scopes = item.Dependencies.Values.SelectMany(x => x.Values.SelectMany(y => y.Select(z => z.Scope))).Distinct().OfType<IInternalMutableScope>().ToArray();
                    State.ExpressionSynchronizationCache.Synchronize(scopes, item.Id);
                    item.DisposeActions.Add(() => State.ExpressionSynchronizationCache.StopSynchronization(scopes, item.Id));
                }

                var expressionChangedEvent = new ScopeExpressionChangedEvent(item.Id, evaluation.Result, evaluation.Status, evaluation.Dependencies, sw.Elapsed);

                logger.LogDebug("Sending expression changed: {expression}", expressionChangedEvent);
                Hub.Post(expressionChangedEvent, o => o.WithTarget(MessageTargets.Subscribers));
            }
            catch (Exception ex)
            {
                logger.LogInformation("Error executing: {exception}", ex);
                var expressionChangedEvent = new ScopeExpressionChangedEvent(item.Id, ex, ExpressionChangedStatus.Error, null, sw.Elapsed, ex);
                Hub.Post(expressionChangedEvent, o => o.ResponseFor(request));
            }
        }, item.Id);

        Hub.Schedule(enqueueRequest.Action);

        return request.Processed();
    }



    private static Action<object, object> CreatePropertySetter((Type Type, string Property) tuple)
    {
        var (type, propertyName) = tuple;
        var property = type.GetProperty(propertyName);
        if (property == null)
            throw new ArgumentException($"Property {propertyName} not found");
        if (!property.CanWrite)
            throw new ArgumentException($"Property {propertyName} has no setter");

        var scopeParam = Expression.Parameter(typeof(object));
        var valueParam = Expression.Parameter(typeof(object));
        var changeTypeMethod = typeof(Convert).GetMethods(BindingFlags.Public | BindingFlags.Static)
                                              .First(x => x.Name == nameof(Convert.ChangeType) && ScopeReflectionExtensions.GetParameterTypes(x).SequenceEqual(new[] { typeof(object), typeof(Type) }));
        var convertExpression = Expression.Call(changeTypeMethod, valueParam, Expression.Constant(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType));
        var valueExpr = Expression.Convert(Expression.Condition(Expression.Or(
                                                                              Expression.TypeIs(valueParam, property.PropertyType),
                                                                              Expression.ReferenceEqual(valueParam, Expression.Constant(null))),
                                                                valueParam,
                                                                convertExpression
                                                               ), property.PropertyType);

        return Expression.Lambda<Action<object, object>>(
                                                         Expression.Assign(Expression.Property(Expression.Convert(scopeParam, type), property), valueExpr),
                                                         scopeParam,
                                                         valueParam
                                                        ).Compile();
    }



    public object GetScope(Guid id)
    {
        return scopeRegistry.GetScope(id);
    }

    private IMessageDelivery Unsubscribe(IMessageDelivery request, IMutableScope scope)
    {
        if (!State.SubscribedScopes.TryGetValue(scope, out var inner))
            return request.Ignored();

        inner = inner.Remove(request.Sender);
        if (inner.Count == 0)
            UpdateState(s => s with { SubscribedScopes = s.SubscribedScopes.Remove(scope) });
        else
            UpdateState(s => s with {SubscribedScopes = s.SubscribedScopes.SetItem(scope, inner)});

        return request.Processed();
    }

    IMessageDelivery IMessageHandler<GetRequest<IApplicationScope>>.HandleMessage(IMessageDelivery<GetRequest<IApplicationScope>> request)
    {
        Hub.Post(applicationScope, o => o.ResponseFor(request));
        return request.Processed();
    }

    IMessageDelivery IMessageHandler<DisposeScopeRequest>.HandleMessage(IMessageDelivery<DisposeScopeRequest> request)
    {
        var scope = request.Message.Scope;
        var ret = Unsubscribe(request, scope);
        scope.Dispose();
        return ret;
    }

    private void RefreshScope(IInternalMutableScope scope)
    {
        try
        {
            scope.Refresh();
        }
        catch (Exception)
        {
            // do nothing
        }
    }

    private void OnScopeInvalidated(object sender, IInternalMutableScope invalidated)
    {
        RefreshScope(invalidated);
    }

    private void TrackPropertyChanged(object scope)
    {
        if (scope is IInternalMutableScope mutableScope)
        {
            UpdateState(s => s with{Tracked = s.Tracked.Add(mutableScope) });
            mutableScope.ScopePropertyChanged += OnScopePropertyChanged;
            mutableScope.ScopeInvalidated += OnScopeInvalidated;
        }
    }

    private void UnsubscribeTracking()
    {
        foreach (var scope in State.Tracked)
        {
            scope.ScopePropertyChanged -= OnScopePropertyChanged;
            scope.ScopeInvalidated -= OnScopeInvalidated;
        }
    }

    public override Task DisposeAsync()
    {
        UnsubscribeTracking();
        return base.DisposeAsync();
    }

}

public record ApplicationScopeState
{
    public ImmutableHashSet<IInternalMutableScope> Tracked { get; init; } = ImmutableHashSet<IInternalMutableScope>.Empty;
    public ImmutableDictionary<object, ImmutableHashSet<object>> SubscribedScopes { get; init; } = ImmutableDictionary<object, ImmutableHashSet<object>>.Empty;
    public ImmutableDictionary<(object address, string ScopeType, object Identity), object> SynchronizedByTypeAndIdentity { get; init; } = ImmutableDictionary<(object address, string ScopeType, object Identity), object>.Empty;
    public ImmutableDictionary<(object Address, string Id), object> SynchronizedById { get; init; } = ImmutableDictionary<(object Address, string Id), object>.Empty;
    public ImmutableDictionary<string, ExpressionRegistryItem> RegisteredExpressions { get; init; } = ImmutableDictionary<string, ExpressionRegistryItem>.Empty;
    public ExpressionSynchronizationCache ExpressionSynchronizationCache { get; } = new();
}