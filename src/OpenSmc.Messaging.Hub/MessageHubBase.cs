﻿using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Reflection;
using OpenSmc.Serialization;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Messaging;

public abstract class MessageHubBase<TAddress> : IMessageHandlerRegistry, IAsyncDisposable
{
    [Inject]protected IEventsRegistry EventsRegistry;
    public virtual TAddress Address { get;  }
    protected readonly LinkedList<RegistryRule> Rules = new();

    protected readonly IMessageService MessageService;
    private readonly ConcurrentDictionary<Type, List<AsyncDelivery>> registeredTypes = new();
    public virtual IMessageHub Hub { get; }
    private readonly IDisposable deferral;

    protected MessageHubBase(IMessageHub hub)
        :this(hub.ServiceProvider)
    {
        Hub = hub;
        hub.RegisterHandlersFromInstance(this);
        Address = (TAddress)hub.Address;
    }
    protected internal MessageHubBase(IServiceProvider serviceProvider)
    {
        serviceProvider.Buildup(this);
        MessageService = serviceProvider.GetRequiredService<IMessageService>();
        deferral = MessageService.Defer(IsDeferred);
        MessageService.Schedule(StartAsync);

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


    protected virtual Task StartAsync()
    {
        deferral.Dispose();
        return Task.CompletedTask;
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
            {
                EventsRegistry.WithEvent(type);
            }

            if (registry.Type.IsGenericType)
            {
                foreach (var genericType in registry.Type.GetGenericArguments())
                    EventsRegistry.WithEvent(genericType);
            }
        }
    }


    private TypeAndHandler GetTypeAndHandler(Type type, object instance)
    {
        if (!type.IsGenericType || !MessageHubPluginExtensions.HandlerTypes.Contains(type.GetGenericTypeDefinition()))
            return null;
        var genericArgs = type.GetGenericArguments();
        var messageType = genericArgs.First();
        return new(messageType, CreateAsyncDelivery(messageType, type, instance));
    }


    private AsyncDelivery CreateAsyncDelivery(Type messageType, Type interfaceType, object instance)
    {
        var deliveryType = typeof(IMessageDelivery<>).MakeGenericType(messageType);
        var prm = Expression.Parameter(typeof(IMessageDelivery));

        var handlerCall = Expression.Call(Expression.Constant(instance, interfaceType),
            interfaceType.GetMethods(BindingFlags.Instance | BindingFlags.Public).First(),
            Expression.Convert(prm, deliveryType)
        );

        if (interfaceType.GetGenericTypeDefinition() == typeof(IMessageHandler<>))
            handlerCall = Expression.Call(null, MessageHubPluginExtensions.TaskFromResultMethod, handlerCall);

        var lambda = Expression.Lambda<Func<IMessageDelivery, Task<IMessageDelivery>>>(
            handlerCall, prm
        ).Compile();
        return d => lambda(d);
    }

    private record TypeAndHandler(Type Type, AsyncDelivery Action);






    protected virtual bool DefaultFilter(IMessageDelivery d) => true;

    public virtual bool Filter(IMessageDelivery d)
    {
        return d.State == MessageDeliveryState.Submitted && (d.Target == null || d.Target.Equals(Address));
    }


    public async Task<IMessageDelivery> DeliverMessageAsync(IMessageDelivery delivery)
    {
        if (!Filter(delivery) || !Rules.Any())
            return delivery;

        return await DeliverMessage(delivery.Submitted(), Rules.First);
    }

    public virtual bool IsDeferred(IMessageDelivery delivery)
    {
        return registeredTypes.ContainsKey(delivery.Message.GetType());
    }

    public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery, LinkedListNode<RegistryRule> node)
    {
        delivery = await node.Value.Rule(delivery);

        if (node.Next == null)
            return delivery;

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

    internal IMessageHandlerRegistry RegisterAfter(LinkedListNode<RegistryRule> node, AsyncDelivery delivery)
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
    public virtual Task DisposeAsync()
    {
        return Task.CompletedTask;
    }


}

public record RegistryRule(AsyncDelivery Rule)
{
    private DeliveryFilter Filter { get; init; }
}