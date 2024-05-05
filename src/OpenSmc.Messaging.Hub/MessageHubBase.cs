using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging.Serialization;
using OpenSmc.Reflection;
using OpenSmc.Serialization;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Messaging;

public abstract class MessageHubBase<TAddress> : IMessageHandlerRegistry, IAsyncDisposable
{
    protected ITypeRegistry TypeRegistry;
    public virtual TAddress Address { get; }
    protected readonly LinkedList<AsyncDelivery> Rules = new();
    private readonly HashSet<Type> registeredTypes = new();

    protected readonly IMessageService MessageService;

    private ImmutableList<(
        Func<IMessageDelivery, bool> Applies,
        AsyncDelivery Delivery
    )> messageHandlers = ImmutableList<(
        Func<IMessageDelivery, bool> Applies,
        AsyncDelivery Delivery
    )>.Empty;
    public virtual IMessageHub Hub { get; }

    protected MessageHubBase(IMessageHub hub)
        : this(hub.ServiceProvider)
    {
        Hub = hub;
        Address = (TAddress)hub.Address;
    }

    protected internal MessageHubBase(IServiceProvider serviceProvider)
    {
        serviceProvider.Buildup(this);
        MessageService = serviceProvider.GetRequiredService<IMessageService>();
        TypeRegistry = serviceProvider.GetRequiredService<ITypeRegistry>();
        InitializeTypes(this);
    }

    private void InitializeTypes(object instance)
    {
        foreach (
            var registry in instance
                .GetType()
                .GetAllInterfaces()
                .Select(i => GetTypeAndHandler(i, instance))
                .Where(x => x != null)
        )
        {
            if (registry.Action != null)
                Register(registry.Action, d => registry.Type.IsAssignableFrom(d.Message.GetType()));

            TypeRegistry.WithType(registry.Type);
            registeredTypes.Add(registry.Type);

            var types = registry
                .Type.GetAllInterfaces()
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
        if (
            !type.IsGenericType
            || !MessageHubPluginExtensions.HandlerTypes.Contains(type.GetGenericTypeDefinition())
        )
            return null;
        var genericArgs = type.GetGenericArguments();

        var cancellationToken = new CancellationTokenSource().Token; // todo: think how to handle this
        if (type.GetGenericTypeDefinition() == typeof(IMessageHandler<>))
            return new(
                genericArgs.First(),
                CreateDelivery(genericArgs.First(), type, instance, null)
            );
        if (type.GetGenericTypeDefinition() == typeof(IMessageHandlerAsync<>))
            return new(
                genericArgs.First(),
                CreateDelivery(
                    genericArgs.First(),
                    type,
                    instance,
                    Expression.Constant(cancellationToken)
                )
            );

        return null;
    }

    private AsyncDelivery CreateDelivery(
        Type messageType,
        Type interfaceType,
        object instance,
        Expression cancellationToken
    )
    {
        var prm = Expression.Parameter(typeof(IMessageDelivery));
        var cancellationTokenPrm = Expression.Parameter(typeof(CancellationToken));

        var expressions = new List<Expression>
        {
            Expression.Convert(prm, typeof(IMessageDelivery<>).MakeGenericType(messageType))
        };
        if (cancellationToken != null)
            expressions.Add(cancellationToken);
        var handlerCall = Expression.Call(
            Expression.Constant(instance, interfaceType),
            interfaceType.GetMethods(BindingFlags.Instance | BindingFlags.Public).First(),
            expressions
        );

        if (interfaceType.GetGenericTypeDefinition() == typeof(IMessageHandler<>))
            handlerCall = Expression.Call(
                null,
                MessageHubPluginExtensions.TaskFromResultMethod,
                handlerCall
            );

        var lambda = Expression
            .Lambda<Func<IMessageDelivery, CancellationToken, Task<IMessageDelivery>>>(
                handlerCall,
                prm,
                cancellationTokenPrm
            )
            .Compile();
        return (d, c) => lambda(d, c);
    }

    private record TypeAndHandler(Type Type, AsyncDelivery Action);

    protected virtual bool DefaultFilter(IMessageDelivery d) => true;

    public virtual bool Filter(IMessageDelivery d)
    {
        return d.State == MessageDeliveryState.Submitted
            && (d.Target == null || d.Target.Equals(Address));
    }

    public virtual async Task<IMessageDelivery> DeliverMessageAsync(
        IMessageDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        if (!Filter(delivery) || !Rules.Any())
            return delivery;

        return await DeliverMessageAsync(delivery.Submitted(), Rules.First, cancellationToken);
    }

    public virtual bool IsDeferred(IMessageDelivery delivery)
    {
        return (Hub.Address.Equals(delivery.Target) || delivery.Target == null)
            && registeredTypes.Any(type => delivery.Message.GetType().IsInstanceOfType(type));
    }

    public async Task<IMessageDelivery> DeliverMessageAsync(
        IMessageDelivery delivery,
        LinkedListNode<AsyncDelivery> node,
        CancellationToken cancellationToken
    )
    {
        delivery = await node.Value.Invoke(delivery, cancellationToken);

        if (node.Next == null)
            return delivery;

        return await DeliverMessageAsync(delivery, node.Next, cancellationToken);
    }

    public IMessageHandlerRegistry Register<TMessage>(SyncDelivery<TMessage> action) =>
        Register(action, DefaultFilter);

    public IMessageHandlerRegistry Register<TMessage>(AsyncDelivery<TMessage> action) =>
        Register(action, DefaultFilter);

    public IMessageHandlerRegistry Register<TMessage>(
        SyncDelivery<TMessage> action,
        DeliveryFilter<TMessage> filter
    )
    {
        return Register((d, _) => Task.FromResult(action(d)), filter);
    }

    public IMessageHandlerRegistry RegisterInherited<TMessage>(
        AsyncDelivery<TMessage> action,
        DeliveryFilter<TMessage> filter = null
    )
    {
        Rules.AddLast(
            new LinkedListNode<AsyncDelivery>(
                (d, c) =>
                    d is IMessageDelivery<TMessage> md && (filter?.Invoke(md) ?? true)
                        ? action(md, c)
                        : Task.FromResult(d)
            )
        );
        return this;
    }

    public IMessageHandlerRegistry Register(SyncDelivery delivery) =>
        Register((d, _) => Task.FromResult(delivery(d)));

    public IMessageHandlerRegistry Register(AsyncDelivery delivery)
    {
        Rules.AddLast(new LinkedListNode<AsyncDelivery>(delivery));
        return this;
    }

    public IMessageHandlerRegistry RegisterInherited<TMessage>(
        SyncDelivery<TMessage> action,
        DeliveryFilter<TMessage> filter = null
    ) => RegisterInherited((d, _) => Task.FromResult(action(d)), filter);

    public IMessageHandlerRegistry Register<TMessage>(
        AsyncDelivery<TMessage> action,
        DeliveryFilter<TMessage> filter
    )
    {
        TypeRegistry.WithType(typeof(TMessage));
        return Register(
            (d, c) => action((MessageDelivery<TMessage>)d, c),
            d => d is IMessageDelivery<TMessage> md && filter(md)
        );
    }

    public IMessageHandlerRegistry Register(Type tMessage, AsyncDelivery action)
    {
        registeredTypes.Add(tMessage);
        return Register(action, d => d.Message.GetType().IsInstanceOfType(tMessage));
    }

    public IMessageHandlerRegistry Register(AsyncDelivery action, DeliveryFilter filter)
    {
        Rules.AddFirst(
            (delivery, cancellationToken) => WrapFilter(delivery, action, filter, cancellationToken)
        );
        return this;
    }

    private Task<IMessageDelivery> WrapFilter(
        IMessageDelivery delivery,
        AsyncDelivery action,
        DeliveryFilter filter,
        CancellationToken cancellationToken
    )
    {
        if (filter(delivery))
            return action(delivery, cancellationToken);
        return Task.FromResult(delivery);
    }

    public IMessageHandlerRegistry Register(Type tMessage, SyncDelivery action) =>
        Register(tMessage, action, _ => true);

    public IMessageHandlerRegistry Register(
        Type tMessage,
        SyncDelivery action,
        DeliveryFilter filter
    ) =>
        Register(
            tMessage,
            (d, _) =>
            {
                d = action(d);
                return Task.FromResult(d);
            },
            filter
        );

    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();

    public virtual Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public IMessageHandlerRegistry Register(
        Type tMessage,
        AsyncDelivery action,
        DeliveryFilter filter
    )
    {
        registeredTypes.Add(tMessage);
        TypeRegistry.WithType(tMessage);
        return Register(
            (d, c) => action(d, c),
            d => d.Message.GetType().IsInstanceOfType(tMessage) && filter(d)
        );
    }
}

public record RegistryRule(AsyncDelivery Rule);
