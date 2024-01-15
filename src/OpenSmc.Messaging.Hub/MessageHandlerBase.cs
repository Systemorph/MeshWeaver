using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using OpenSmc.Reflection;
using OpenSmc.Serialization;

namespace OpenSmc.Messaging.Hub;

public abstract class MessageHandlerBase : IMessageHandler, IMessageHandlerRegistry
{
    protected object Me { get; private set; }
    protected readonly IEventsRegistry EventsRegistry;


    private readonly ConcurrentDictionary<string, List<AsyncDelivery>> callbacks = new();

    protected IMessageDelivery RegisterCallback<TMessage, TResponse>(IMessageDelivery<TMessage> request, SyncDelivery<TResponse> callback, CancellationToken cancellationToken = default)
        where TMessage : IRequest<TResponse>
        => RegisterCallback<TMessage, TResponse>(request, d => Task.FromResult(callback(d)), cancellationToken);

    protected IMessageDelivery RegisterCallback<TMessage, TResponse>(IMessageDelivery<TMessage> request, AsyncDelivery<TResponse> callback, CancellationToken cancellationToken = default)
        where TMessage : IRequest<TResponse>
    {
        RegisterCallback(request, d => callback((IMessageDelivery<TResponse>)d), cancellationToken);
        return request.Forwarded();
    }

    protected Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, SyncDelivery callback, CancellationToken cancellationToken = default)
        => RegisterCallback(delivery, d => Task.FromResult(callback(d)), cancellationToken);

    // ReSharper disable once UnusedMethodReturnValue.Local
    public Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, AsyncDelivery callback, CancellationToken cancellationToken = default)

    {
        // TODO V10: this should react to IMessageDelivery of IRequest<TMessage> in order to find missing routes etc (2023-08-23, Andrei Sirotenko)
        // if message status is not processed => set TaskCompletionSource to Exception state.
        bool DeliveryFilter(IMessageDelivery d) => d.Properties.TryGetValue(PostOptions.RequestId, out var request) && request.Equals(delivery.Id);


        var tcs = new TaskCompletionSource<IMessageDelivery>(cancellationToken);

        async Task<IMessageDelivery> ResolveCallback(IMessageDelivery d)
        {
            var ret = await callback(d);
            tcs.SetResult(ret);
            return ret;
        }

        callbacks.GetOrAdd(delivery.Id, _ => new()).Add(ResolveCallback);

        async Task<IMessageDelivery> WrapWithTimeout(Task<IMessageDelivery> deliveryTask)
        {
            var timeout = 999999999;
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            await Task.WhenAny(timeoutTask, deliveryTask);
            if (deliveryTask.IsCompleted)
                return deliveryTask.Result;

            HandleTimeout(delivery);
            throw new TimeoutException($"Timeout of {timeout} was exceeded waiting for response for message {delivery.Id} to {delivery.Target}");
        }

        return WrapWithTimeout(tcs.Task);
    }
    private async Task<IMessageDelivery> HandleCallbacks(IMessageDelivery delivery)
    {
        if (delivery.Properties.TryGetValue(PostOptions.RequestId, out var requestId) && callbacks.TryRemove(requestId.ToString(), out var myCallbacks))
            foreach (var callback in myCallbacks)
                await callback(delivery);


        return delivery;
    }

    private void HandleTimeout(IMessageDelivery delivery)
    {
        // TODO SMCv2: Add proper error handling, e.g. logging, informing upstream requesters, etc. (2023/08/27, Roland Buergi)
    }

    protected IMessageService MessageService { get; private set; }
    protected readonly LinkedList<RegistryRule> Rules;

    protected MessageHandlerBase(IEventsRegistry eventsRegistry)
    {
        EventsRegistry = eventsRegistry;
        InitializeTypes(this);

        Rules = new LinkedList<RegistryRule>(new RegistryRule[]
                                             {
                                                 new(HandleCallbacks),
                                                 new(async delivery =>
                                                     {
                                                         if (delivery?.Message == null || !registeredTypes.TryGetValue(delivery.Message.GetType(), out var registry))
                                                             return delivery;
                                                         foreach (var asyncDelivery in registry)
                                                             delivery = await asyncDelivery.Invoke(delivery);
                                                         return delivery;
                                                     })
                                             });
    }


    private void InitializeTypes(object instance)
    {
        foreach (var registry in instance.GetType().GetAllInterfaces().Select(i => GetTypeAndHandler(i, instance)).Where(x => x != null))
        {
            if (registry.Action != null)
                Register(registry.Type, registry.Action, _ => true);

            EventsRegistry.WithEvent(registry.Type);

            var types = registry.Type.GetAllInterfaces()
                                .Where(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IRequest<>))
                                .SelectMany(x => x.GetGenericArguments());

            foreach (var type in types)
                EventsRegistry.WithEvent(type);

            if (registry.Type.IsGenericType)
            {
                foreach (var genericType in registry.Type.GetGenericArguments())
                    EventsRegistry.WithEvent(genericType);
            }
        }
    }

    private static readonly HashSet<Type> HandlerTypes = new() { typeof(IMessageHandler<>), typeof(IMessageHandlerAsync<>) };

    private TypeAndHandler GetTypeAndHandler(Type type, object instance)
    {
        if (!type.IsGenericType || !HandlerTypes.Contains(type.GetGenericTypeDefinition()))
            return null;
        var genericArgs = type.GetGenericArguments();
        var messageType = genericArgs.First();
        return new(messageType, CreateAsyncDelivery(messageType, type, instance));
    }

    private static readonly MethodInfo TaskFromResultMethod = ReflectionHelper.GetStaticMethod(() => Task.FromResult<IMessageDelivery>(null));

    private AsyncDelivery CreateAsyncDelivery(Type messageType, Type interfaceType, object instance)
    {
        var deliveryType = typeof(IMessageDelivery<>).MakeGenericType(messageType);
        var prm = Expression.Parameter(typeof(IMessageDelivery));

        var handlerCall = Expression.Call(Expression.Constant(instance, interfaceType),
                                          interfaceType.GetMethods(BindingFlags.Instance | BindingFlags.Public).First(),
                                          Expression.Convert(prm, deliveryType)
                                         );

        if (interfaceType.GetGenericTypeDefinition() == typeof(IMessageHandler<>))
            handlerCall = Expression.Call(null, TaskFromResultMethod, handlerCall);

        var lambda = Expression.Lambda<Func<IMessageDelivery, Task<IMessageDelivery>>>(
                                                                                       handlerCall, prm
                                                                                      ).Compile();
        return d => lambda(d);
    }

    private record TypeAndHandler(Type Type, AsyncDelivery Action);

    protected virtual bool Filter(IMessageDelivery d)
    {
        return d.State == MessageDeliveryState.Submitted && (d.Target == null || d.Target.Equals(Me));
    }

    protected virtual bool DefaultFilter(IMessageDelivery d) => true;


    protected virtual async Task<IMessageDelivery> HandleMessageAsync(IMessageDelivery delivery)
    {
        var node = Rules.First;
        if (node != null)
            return await DeliverMessage(delivery.Submitted(), node);
        return delivery;
    }

    public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery, LinkedListNode<RegistryRule> node)
    {
        if (node == null || !Filter(delivery))
            return delivery;

        delivery = await node.Value.Rule(delivery);
        return await DeliverMessage(delivery, node.Next);
    }



    Task<IMessageDelivery> IMessageHandler.HandleMessageAsync(IMessageDelivery delivery)
    {
        if (Filter(delivery))
            return HandleMessageAsync(delivery);
        return Task.FromResult(delivery);
    }

    public virtual void Connect(IMessageService messageService, object address)
    {
        MessageService = messageService;
        Me = address;
    }


    public IMessageHandlerRegistry Register<TMessage>(SyncDelivery<TMessage> action) => Register(action, DefaultFilter);
    public IMessageHandlerRegistry Register<TMessage>(AsyncDelivery<TMessage> action) => Register(action, DefaultFilter);

    public IMessageHandlerRegistry Register<TMessage>(SyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter)
    {
        return Register(d => Task.FromResult(action(d)), filter);
    }

    public IMessageHandlerRegistry RegisterInherited<TMessage>(AsyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter = null)
    {
        Rules.AddLast(new LinkedListNode<RegistryRule>(new RegistryRule(d => d is IMessageDelivery<TMessage> md && (filter?.Invoke(md) ?? true) ? action(md) : Task.FromResult(d))));
        return this;
    }

    public IMessageHandlerRegistry Register(SyncDelivery delivery) =>
        Register(d => Task.FromResult(delivery(d)));

    public IMessageHandlerRegistry Register(AsyncDelivery delivery)
    {
        Rules.AddLast(new LinkedListNode<RegistryRule>(new RegistryRule(delivery)));
        return this;
    }

    public IMessageHandlerRegistry RegisterHandlersFromInstance(object instance)
    {
        InitializeTypes(instance);
        return this;
    }

    public IMessageHandlerRegistry RegisterInherited<TMessage>(SyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter = null)
        => RegisterInherited(d => Task.FromResult(action(d)), filter);

    public IMessageHandlerRegistry Register<TMessage>(AsyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter)
    {
        return Register(typeof(TMessage), d => action((MessageDelivery<TMessage>)d), d => filter((IMessageDelivery<TMessage>)d));
    }

    private readonly ConcurrentDictionary<Type, List<AsyncDelivery>> registeredTypes = new();

    public IMessageHandlerRegistry Register(Type tMessage, AsyncDelivery action) => Register(tMessage, action, _ => true);

    public IMessageHandlerRegistry Register(Type tMessage, AsyncDelivery action, DeliveryFilter filter)
    {
        EventsRegistry.WithEvent(tMessage);
        var list = registeredTypes.GetOrAdd(tMessage, _ => new());
        list.Add(delivery => WrapFilter(delivery, action, filter));
        return this;
    }

    private Task<IMessageDelivery> WrapFilter(IMessageDelivery delivery, AsyncDelivery action, DeliveryFilter filter)
    {
        if (filter(delivery))
            return action(delivery);
        return Task.FromResult(delivery);
    }

    public IMessageHandlerRegistry Register(Type tMessage, SyncDelivery action) => Register(tMessage, action, _ => true);

    public IMessageHandlerRegistry Register(Type tMessage, SyncDelivery action, DeliveryFilter filter) => Register(tMessage,
                                                                                                                   d =>
                                                                                                                   {
                                                                                                                       d = action(d);
                                                                                                                       return Task.FromResult(d);
                                                                                                                   },
                                                                                                                   filter);

    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();

    public virtual async Task DisposeAsync()
    {
        var sw = Stopwatch.StartNew();
        while (callbacks.Any() && sw.ElapsedMilliseconds < 5000)
            await Task.Delay(100);
    }
}

public record RegistryRule(AsyncDelivery Rule);