using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Reflection;
using OpenSmc.Serialization;
using OpenSmc.ServiceProvider;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace OpenSmc.Messaging.Hub;

public class MessageHubPlugin<TPlugin> : IMessageHubPlugin, IMessageHandlerRegistry
    where TPlugin : MessageHubPlugin<TPlugin>
{
    protected virtual IMessageHub Hub { get; private set; }

    protected readonly IEventsRegistry EventsRegistry;
    protected readonly LinkedList<RegistryRule> Rules;

    protected MessageHubPlugin(IServiceProvider serviceProvider)
    {
        EventsRegistry = serviceProvider.GetRequiredService<IEventsRegistry>(); ;
        InitializeTypes(this);

        Rules = new LinkedList<RegistryRule>(new RegistryRule[]
        {
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
        if (EventsRegistry == null)
            return;

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


    protected virtual bool DefaultFilter(IMessageDelivery d) => true;

    protected virtual bool Filter(IMessageDelivery d)
    {
        return d.State == MessageDeliveryState.Submitted && (d.Target == null || d.Target.Equals(Hub.Address));
    }


    public async Task<IMessageDelivery> HandleMessageAsync(IMessageDelivery delivery)
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


    public IMessageHandlerRegistry Register<TMessage>(SyncDelivery<TMessage> action) => Register(action, DefaultFilter);
    public IMessageHandlerRegistry Register<TMessage>(AsyncDelivery<TMessage> action) => Register(action, DefaultFilter);

    public IMessageHandlerRegistry Register<TMessage>(SyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter)
    {
        return Register(d => Task.FromResult(action(d)), filter);
    }

    public IMessageHandlerRegistry RegisterInherited<TMessage>(AsyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter = null)
    {
        Rules.AddFirst(new LinkedListNode<RegistryRule>(new RegistryRule(d => d is IMessageDelivery<TMessage> md && (filter?.Invoke(md) ?? true) ? action(md) : Task.FromResult(d))));
        return this;
    }

    public IMessageHandlerRegistry Register(SyncDelivery delivery) =>
        Register(d => Task.FromResult(delivery(d)));

    public IMessageHandlerRegistry Register(AsyncDelivery delivery)
    {
        Rules.AddFirst(new LinkedListNode<RegistryRule>(new RegistryRule(delivery)));
        return this;
    }

    public IMessageHandlerRegistry RegisterAfter(LinkedListNode<RegistryRule> node, AsyncDelivery delivery)
    {
        Rules.AddAfter(node, new LinkedListNode<RegistryRule>(new RegistryRule(delivery)));
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


    public virtual  ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public virtual Task InitializeAsync(IMessageHub hub)
    {
        Hub = hub;
        hub.ServiceProvider.Buildup(this);
        hub.RegisterHandlersFromInstance(this);
        return Task.CompletedTask;
    }
 }


public class MessageHubPlugin<TPlugin, TState> : MessageHubPlugin<TPlugin>, IAsyncDisposable
    where TPlugin : MessageHubPlugin<TPlugin, TState>
{
    public TState State { get; private set; }
    protected TPlugin This => (TPlugin)this;

    protected TPlugin UpdateState(Func<TState, TState> changes)
    {
        State = changes.Invoke(State);
        return This;
    }

    public override async Task InitializeAsync(IMessageHub hub)
    {
        await base.InitializeAsync(hub);
        if (State == null)
        {
            var constructor = typeof(TState).GetConstructor(Array.Empty<Type>());
            if (constructor != null)
                InitializeState(Activator.CreateInstance<TState>());
        }
    }

    public virtual TPlugin InitializeState(TState state)
    {
        State = state;
        return This;
    }

    public virtual ValueTask DisposeAsync()
    {
        return default;
    }

    protected MessageHubPlugin(IServiceProvider serviceProvider) : base(serviceProvider)
    {
    }
}
