using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Reflection;
using OpenSmc.Serialization;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Messaging;

public abstract class MessageHubBase<TAddress> : IMessageHandlerRegistry, IAsyncDisposable
{
    [Inject]protected ITypeRegistry TypeRegistry;
    public virtual TAddress Address { get;  }
    protected readonly LinkedList<AsyncDelivery> Rules;

    protected readonly IMessageService MessageService;
    private readonly ConcurrentDictionary<Type, List<AsyncDelivery>> registeredTypes = new();
    public virtual IMessageHub Hub { get; }

    protected MessageHubBase(IMessageHub hub)
        :this(hub.ServiceProvider)
    {
        Hub = hub;
        Address = (TAddress)hub.Address;

    }
    protected internal MessageHubBase(IServiceProvider serviceProvider)
    {
        serviceProvider.Buildup(this);
        MessageService = serviceProvider.GetRequiredService<IMessageService>();
        InitializeTypes(this);

        Rules = new LinkedList<AsyncDelivery>(new AsyncDelivery[]
        {
            async (delivery, cancellationToken) =>
            {
                if (delivery?.Message == null || !registeredTypes.TryGetValue(delivery.Message.GetType(), out var registry))
                    return delivery;
                foreach (var asyncDelivery in registry)
                    delivery = await asyncDelivery.Invoke(delivery, cancellationToken);
                return delivery;
            }
        });
    }



    private void InitializeTypes(object instance)
    {
        if (TypeRegistry == null)
            return;

        foreach (var registry in instance.GetType().GetAllInterfaces().Select(i => GetTypeAndHandler(i, instance)).Where(x => x != null))
        {
            if (registry.Action != null)
                Register(registry.Type, registry.Action, _ => true);

            TypeRegistry.WithType(registry.Type);

            var types = registry.Type.GetAllInterfaces()
                .Where(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IRequest<>))
                .SelectMany(x => x.GetGenericArguments());

            foreach (var type in types)
            {
                TypeRegistry.WithType(type);
            }

            if (registry.Type.IsGenericType)
            {
                foreach (var genericType in registry.Type.GetGenericArguments())
                    TypeRegistry.WithType(genericType);
            }
        }
    }


    private TypeAndHandler GetTypeAndHandler(Type type, object instance)
    {
        if (!type.IsGenericType || !MessageHubPluginExtensions.HandlerTypes.Contains(type.GetGenericTypeDefinition()))
            return null;
        var genericArgs = type.GetGenericArguments();

        var cancellationToken = new CancellationTokenSource().Token; // todo: think how to handle this
        if (type.GetGenericTypeDefinition() == typeof(IMessageHandler<>))
            return new(genericArgs.First(), CreateDelivery(genericArgs.First(), type, instance, null));
        if (type.GetGenericTypeDefinition() == typeof(IMessageHandlerAsync<>))
            return new(genericArgs.First(), CreateDelivery(genericArgs.First(), type, instance, Expression.Constant(cancellationToken)));

        return null;
    }


    private AsyncDelivery CreateDelivery(Type messageType, Type interfaceType, object instance, Expression cancellationToken)
    {
        var prm = Expression.Parameter(typeof(IMessageDelivery));
        var cancellationTokenPrm = Expression.Parameter(typeof(CancellationToken));


        var expressions = new List<Expression>
        {
            Expression.Convert(prm, typeof(IMessageDelivery<>).MakeGenericType(messageType))
        };
        if(cancellationToken != null)
            expressions.Add(cancellationToken);
        var handlerCall = Expression.Call(Expression.Constant(instance, interfaceType),
            interfaceType.GetMethods(BindingFlags.Instance | BindingFlags.Public).First(),
            expressions
        );

        if (interfaceType.GetGenericTypeDefinition() == typeof(IMessageHandler<>))
            handlerCall = Expression.Call(null, MessageHubPluginExtensions.TaskFromResultMethod, handlerCall);

        var lambda = Expression.Lambda<Func<IMessageDelivery, CancellationToken, Task<IMessageDelivery>>>(
            handlerCall, prm, cancellationTokenPrm
        ).Compile();
        return (d, c) => lambda(d,c);
    }

    private record TypeAndHandler(Type Type, AsyncDelivery Action);






    protected virtual bool DefaultFilter(IMessageDelivery d) => true;

    public virtual bool Filter(IMessageDelivery d)
    {
        return d.State == MessageDeliveryState.Submitted && (d.Target == null || d.Target.Equals(Address));
    }


    public virtual async Task<IMessageDelivery> DeliverMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        if (!Filter(delivery) || !Rules.Any())
            return delivery;

        return await DeliverMessageAsync(delivery.Submitted(), Rules.First, cancellationToken);
    }


    public virtual bool IsDeferred(IMessageDelivery delivery)
    {
        return (Hub.Address.Equals(delivery.Target) || delivery.Target == null) 
               && registeredTypes.ContainsKey(delivery.Message.GetType());
    }

    public async Task<IMessageDelivery> DeliverMessageAsync(IMessageDelivery delivery, LinkedListNode<AsyncDelivery> node, CancellationToken cancellationToken)
    {
        delivery = await node.Value.Invoke(delivery, cancellationToken);

        if (node.Next == null)
            return delivery;

        return await DeliverMessageAsync(delivery, node.Next, cancellationToken);
    }


    public IMessageHandlerRegistry Register<TMessage>(SyncDelivery<TMessage> action) => Register(action, DefaultFilter);
    public IMessageHandlerRegistry Register<TMessage>(AsyncDelivery<TMessage> action) => Register(action, DefaultFilter);

    public IMessageHandlerRegistry Register<TMessage>(SyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter)
    {
        return Register((d,_) => Task.FromResult(action(d)), filter);
    }

    public IMessageHandlerRegistry RegisterInherited<TMessage>(AsyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter = null)
    {
        Rules.AddLast(new LinkedListNode<AsyncDelivery>((d, c) => d is IMessageDelivery<TMessage> md && (filter?.Invoke(md) ?? true) ? action(md,c) : Task.FromResult(d)));
        return this;
    }

    public IMessageHandlerRegistry Register(SyncDelivery delivery) =>
        Register((d, _) => Task.FromResult(delivery(d)));

    public IMessageHandlerRegistry Register(AsyncDelivery delivery)
    {
        Rules.AddLast(new LinkedListNode<AsyncDelivery>(delivery));
        return this;
    }


    public IMessageHandlerRegistry RegisterInherited<TMessage>(SyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter = null)
        => RegisterInherited((d,_) => Task.FromResult(action(d)), filter);

    public IMessageHandlerRegistry Register<TMessage>(AsyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter)
    {
        return Register(typeof(TMessage), (d, c) => action((MessageDelivery<TMessage>)d,c), d => filter((IMessageDelivery<TMessage>)d));
    }


    public IMessageHandlerRegistry Register(Type tMessage, AsyncDelivery action) => Register(tMessage, action, _ => true);

    public IMessageHandlerRegistry Register(Type tMessage, AsyncDelivery action, DeliveryFilter filter)
    {
        TypeRegistry.WithType(tMessage);
        var list = registeredTypes.GetOrAdd(tMessage, _ => new());
        list.Add((delivery , cancellationToken)=> WrapFilter(delivery, action, filter, cancellationToken));
        return this;
    }

    private Task<IMessageDelivery> WrapFilter(IMessageDelivery delivery, AsyncDelivery action, DeliveryFilter filter,
        CancellationToken cancellationToken)
    {
        if (filter(delivery))
            return action(delivery, cancellationToken);
        return Task.FromResult(delivery);
    }

    public IMessageHandlerRegistry Register(Type tMessage, SyncDelivery action) => Register(tMessage, action, _ => true);

    public IMessageHandlerRegistry Register(Type tMessage, SyncDelivery action, DeliveryFilter filter) => Register(tMessage,
        (d,_) =>
        {
            d = action(d);
            return Task.FromResult(d);
        },
        filter);


    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();
    public virtual Task DisposeAsync()
    {
        return Task.CompletedTask;
    }


}

public record RegistryRule(AsyncDelivery Rule);