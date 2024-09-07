using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Disposables;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.Reflection;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

public abstract class MessageHubBase : IMessageHandlerRegistry, IAsyncDisposable
{
    protected ITypeRegistry TypeRegistry { get; }
    public virtual object Address => Hub.Address;
    protected readonly LinkedList<AsyncDelivery> Rules = new();
    private readonly HashSet<Type> registeredTypes = new();

    protected readonly IMessageService MessageService;

    public IMessageHub Hub { get; protected set; }

    public virtual Task StartAsync(IMessageHub hub, CancellationToken cancellationToken)
    {
        Hub = hub;
        return Task.CompletedTask;
    }
    protected ILogger Logger { get; }
    protected internal MessageHubBase(IServiceProvider serviceProvider)
    {
        serviceProvider.Buildup(this);
        Logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());
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
                Register(registry.Action, d => registry.Type.IsInstanceOfType(d.Message));

            registeredTypes.Add(registry.Type);
            WithTypeAndRelatedTypesFor(registry.Type);
        }
    }

    private void WithTypeAndRelatedTypesFor(Type typeToRegister)
    {
        TypeRegistry.WithType(typeToRegister);

        var types = typeToRegister
            .GetAllInterfaces()
            .Where(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IRequest<>))
            .SelectMany(x => x.GetGenericArguments());

        foreach (var type in types)
        {
            TypeRegistry.WithType(type);
        }

        if (typeToRegister.IsGenericType)
        {
            foreach (var genericType in typeToRegister.GetGenericArguments())
                TypeRegistry.WithType(genericType);
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

        var ret = await DeliverMessageAsync(delivery.Submitted(), Rules.First, cancellationToken);
        return ret;
    }

    public virtual bool IsDeferred(IMessageDelivery delivery)
    {
        return (Hub.Address.Equals(delivery.Target) || delivery.Target == null)
            && registeredTypes.Any(type => type.IsInstanceOfType(delivery.Message));
    }

    public async Task<IMessageDelivery> DeliverMessageAsync(
        IMessageDelivery delivery,
        LinkedListNode<AsyncDelivery> node,
        CancellationToken cancellationToken
    )
    {
        delivery = await node.Value.Invoke(delivery, cancellationToken);

        if (node.Next == null)
        {
            Logger.LogDebug("No handler found for {Delivery}", delivery);
            return delivery.Ignored();
        }

        return await DeliverMessageAsync(delivery, node.Next, cancellationToken);
    }

    public IDisposable Register<TMessage>(SyncDelivery<TMessage> action) =>
        Register(action, DefaultFilter);

    public IDisposable Register<TMessage>(AsyncDelivery<TMessage> action) =>
        Register(action, DefaultFilter);

    public IDisposable Register<TMessage>(
        SyncDelivery<TMessage> action,
        DeliveryFilter<TMessage> filter
    )
    {
        return Register((d, _) => Task.FromResult(action(d)), filter);
    }

    public IDisposable RegisterInherited<TMessage>(
        AsyncDelivery<TMessage> action,
        DeliveryFilter<TMessage> filter = null
    )
    {
        var node = new LinkedListNode<AsyncDelivery>(
            (d, c) =>
                d is IMessageDelivery<TMessage> md && (filter?.Invoke(md) ?? true)
                    ? action(md, c)
                    : Task.FromResult(d)
        );
        Rules.AddLast(node);
        return new AnonymousDisposable(() => Rules.Remove(node));
    }

    public IDisposable Register(SyncDelivery delivery) =>
        Register((d, _) => Task.FromResult(delivery(d)));

    public IDisposable Register(AsyncDelivery delivery)
    {
        var node = new LinkedListNode<AsyncDelivery>(delivery);
        Rules.AddLast(node);
        return new AnonymousDisposable(() => Rules.Remove(node));
    }

    public IDisposable RegisterInherited<TMessage>(
        SyncDelivery<TMessage> action,
        DeliveryFilter<TMessage> filter = null
    ) => RegisterInherited((d, _) => Task.FromResult(action(d)), filter);

    public IDisposable Register<TMessage>(
        AsyncDelivery<TMessage> action,
        DeliveryFilter<TMessage> filter
    )
    {
        WithTypeAndRelatedTypesFor(typeof(TMessage));
        return Register(
            (d, c) => action((MessageDelivery<TMessage>)d, c),
            d => (d.Target == null || Address.Equals(d.Target)) && d is IMessageDelivery<TMessage> md && filter(md)
        );
    }

    public IDisposable Register(Type tMessage, AsyncDelivery action)
    {
        registeredTypes.Add(tMessage);
        WithTypeAndRelatedTypesFor(tMessage);
        return Register(action, d => tMessage.IsInstanceOfType(d.Message));
    }

    public IDisposable Register(AsyncDelivery action, DeliveryFilter filter)
    {
        AsyncDelivery rule = (delivery, cancellationToken) =>
            WrapFilter(delivery, action, filter, cancellationToken);
        Rules.AddFirst(rule);
        return new AnonymousDisposable(() => Rules.Remove(rule));
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

    public IDisposable Register(Type tMessage, SyncDelivery action) =>
        Register(tMessage, action, _ => true);

    public IDisposable Register(Type tMessage, SyncDelivery action, DeliveryFilter filter) =>
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

    public IDisposable Register(Type tMessage, AsyncDelivery action, DeliveryFilter filter)
    {
        registeredTypes.Add(tMessage);
        WithTypeAndRelatedTypesFor(tMessage);
        return Register(
            (d, c) => action(d, c),
            d => tMessage.IsInstanceOfType(d.Message) && filter(d)
        );
    }
}

public record RegistryRule(AsyncDelivery Rule);
