using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenSmc.Serialization;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Messaging.Hub;

public static class MessageHubExtensions
{
    public static IMessageHub<TAddress> CreateMessageHub<TAddress>(this IServiceProvider serviceProvider, TAddress address, Func<MessageHubConfiguration, MessageHubConfiguration> configuration)
        => CreateMessageHub<MessageHub<TAddress>, TAddress>(serviceProvider, address, configuration);
    public static IMessageHub<TAddress> CreateMessageHub<THub, TAddress>(this IServiceProvider serviceProvider, TAddress address, Func<MessageHubConfiguration, MessageHubConfiguration> configuration)
        where THub : class, IMessageHub<TAddress>
    {
        var hubSetup = new MessageHubConfiguration(serviceProvider, address);
        return (IMessageHub<TAddress>)configuration(hubSetup).Build<THub>(serviceProvider, address);
    }
}

public record MessageHubConfiguration
{
    public object Address { get; }
    protected readonly IServiceProvider ParentServiceProvider;
    internal Func<AsyncDelivery, ForwardConfiguration> ForwardConfigurationRouteBuilder { get; set; }
    public MessageHubConfiguration(IServiceProvider parentServiceProvider, object address)
    {
        Address = address;
        ParentServiceProvider = parentServiceProvider;
    }

    internal Func<IServiceCollection, IServiceCollection> Services { get; init; } = x => x;

    public IServiceProvider ServiceProvider { get; set; }

    internal ImmutableList<Action<IMessageHub>> DisposeActions { get; init; } = ImmutableList<Action<IMessageHub>>.Empty;

    internal IMessageHub ParentHub { get; init; }
    internal object SynchronizationAddress { get; init; }

    internal ImmutableList<MessageHandlerItem> MessageHandlers { get; init; } = ImmutableList<MessageHandlerItem>.Empty;
    internal Func<string> GetAccessObject { get; init; }
    internal ImmutableList<StartConfiguration> StartConfigurations { get; init; } = ImmutableList<StartConfiguration>.Empty;

    protected internal ImmutableList<Func<IMessageHub, Task>> BuildupActions { get; init; } = ImmutableList<Func<IMessageHub, Task>>.Empty;

    internal ImmutableList<DeliveryFilter> Deferrals { get; init; } = ImmutableList<DeliveryFilter>.Empty;

    internal IMessageHub HubInstance { get; set; }

    public MessageHubConfiguration WithDisposeAction(Action<IMessageHub> disposeAction) => this with { DisposeActions = DisposeActions.Add(disposeAction) };
    public MessageHubConfiguration WithDisposeActions(IEnumerable<Action<IMessageHub>> disposeActions) => this with { DisposeActions = DisposeActions.AddRange(disposeActions) };

    internal MessageHubConfiguration WithParentHub(IMessageHub optParentHubs)
    {
        return this with { ParentHub = optParentHubs };
    }


    internal Func<ForwardConfiguration, ForwardConfiguration> ForwardConfigurationBuilder { get; init; }


    public MessageHubConfiguration WithServices(Func<IServiceCollection, IServiceCollection> configuration)
    {
        return this with { Services = x => configuration(Services(x)) };
    }
    public MessageHubConfiguration SynchronizeFrom(object address) => this with { SynchronizationAddress = address };


    public MessageHubConfiguration WithForwards(Func<ForwardConfiguration, ForwardConfiguration> configuration) => this with { ForwardConfigurationBuilder = x => configuration(ForwardConfigurationBuilder?.Invoke(x) ?? x) };
    // TODO V10: is this Api redundant? (2024/01/22, Dmitry Kalabin)
    //public MessageHubConfiguration WithForwardToTarget<TMessage>(object address, Func<ForwardConfigurationItem<TMessage>, ForwardConfigurationItem<TMessage>> config = null)
    //    => WithForwards(f => f.WithForwardToTarget(address, config));

    public MessageHubConfiguration WithForward<TMessage>(SyncDelivery<TMessage> route, Func<ForwardConfigurationItem<TMessage>, ForwardConfigurationItem<TMessage>> setup = null)
        => WithForwards(f => f.WithForward(route, setup));
    public MessageHubConfiguration WithForward<TMessage>(AsyncDelivery<TMessage> route, Func<ForwardConfigurationItem<TMessage>, ForwardConfigurationItem<TMessage>> setup = null)
        => WithForwards(f => f.WithForward(route, setup));

    public MessageHubConfiguration WithAccessObject(Func<string> getAccessObject)
    {
        return this with { GetAccessObject = getAccessObject };
    }


    protected virtual ServiceCollection ConfigureServices<THub>()
        where THub : class, IMessageHub
    {
        var services = new ServiceCollection();
        services.Replace(ServiceDescriptor.Singleton<IMessageHub, THub>());
        services.Replace(ServiceDescriptor.Singleton<HostedHubsCollection, HostedHubsCollection>());
        services.Replace(ServiceDescriptor.Singleton(typeof(IEventsRegistry),
            sp => new EventsRegistry(ParentServiceProvider.GetService<IEventsRegistry>())));
        services.Replace(ServiceDescriptor.Singleton<IMessageService>(sp => new MessageService(Address,
            sp.GetService<ISerializationService>(), // HACK: GetRequiredService replaced by GetService (16.01.2024, Alexander Yolokhov)
            sp.GetRequiredService<ILogger<MessageService>>()
        )));
        Services.Invoke(services);
        return services;
    }


    public MessageHubConfiguration WithHandler<TMessage>(Func<IMessageHub, IMessageDelivery<TMessage>, IMessageDelivery> delivery) => WithBuildupAction(hub => hub.Register<TMessage>(request => delivery(hub, request)));
    public MessageHubConfiguration WithHandler<TMessage>(Func<IMessageHub, IMessageDelivery<TMessage>, Task<IMessageDelivery>> delivery) => WithBuildupAction(hub => hub.Register<TMessage>(request => delivery(hub, request)));
    public MessageHubConfiguration WithHandler<TMessage>(Func<IMessageHub, IMessageDelivery<TMessage>, IMessageDelivery> delivery, DeliveryFilter<TMessage> filter) => WithBuildupAction(hub => hub.Register(request => delivery(hub, request), filter));
    public MessageHubConfiguration WithHandler<TMessage>(Func<IMessageHub, IMessageDelivery<TMessage>, Task<IMessageDelivery>> delivery, DeliveryFilter<TMessage> filter) => WithBuildupAction(hub => hub.Register(request => delivery(hub, request), filter));


    public MessageHubConfiguration WithBuildupAction(Action<IMessageHub> action) => this with { BuildupActions = BuildupActions.Add(hub => { action(hub); return Task.CompletedTask; }) };
    public MessageHubConfiguration WithBuildupAction(Func<IMessageHub, Task> action) => this with { BuildupActions = BuildupActions.Add(action) };


    public MessageHubConfiguration WithDeferral(DeliveryFilter deferral)
        => this with { Deferrals = Deferrals.Add(deferral) };

    protected void CreateServiceProvider<THub>()
        where THub : class, IMessageHub
    {
        string Tag() => (typeof(THub).IsGenericType ? typeof(THub).FullName : typeof(THub).Name) + Address?.GetHashCode();

        ServiceProvider = ConfigureServices<THub>()
            .SetupModules(ParentServiceProvider, new ModulesBuilder().Add(GetType().Assembly), Tag());
    }

    public virtual IMessageHub Build<THub>(IServiceProvider serviceProvider, object address)
        where THub : class, IMessageHub
    {
        // TODO V10: Check whether this address is already built in hosted hubs collection, if not build. (18.01.2024, Roland Buergi)
        CreateServiceProvider<THub>();
        HubInstance = ServiceProvider.GetRequiredService<IMessageHub>();
        ForwardConfigurationRouteBuilder = routedDelivery => (ForwardConfigurationBuilder ?? (x => x)).Invoke(new ForwardConfiguration());
        //var parentHub = ParentServiceProvider.GetService<IMessageHub>();
        ((MessageHubBase)HubInstance).Initialize(this, /*parentHub*/ null);
        return HubInstance;
    }
}

public record StartConfiguration(object Address)
{
    internal ImmutableList<object> CreationObjects { get; init; } = ImmutableList<object>.Empty;
    public StartConfiguration WithCreateMessage(object instance) => this with { CreationObjects = CreationObjects.Add(instance) };
}

public record RoutedHubConfiguration
{
    public IMessageHub Hub { get; init; }
    internal ImmutableList<MessageRouteConfiguration> MessageRoutes { get; init; }
    internal ImmutableList<Func<IMessageHub, MessageRouteConfiguration>> MessageRouteConfigurationFunctions = ImmutableList<Func<IMessageHub, MessageRouteConfiguration>>.Empty;
    internal ImmutableList<IForwardConfigurationItem> ForwardConfigurationItems { get; init; }
    internal ImmutableList<Func<IMessageHub, IForwardConfigurationItem>> ForwardConfigurationFunctions = ImmutableList<Func<IMessageHub, IForwardConfigurationItem>>.Empty;

    public RoutedHubConfiguration RouteMessage<TMessage>(Func<IMessageDelivery, object> addressMap, Func<MessageRouteConfiguration<TMessage>, MessageRouteConfiguration<TMessage>> config = null)
        => this with
        {
            MessageRouteConfigurationFunctions = MessageRouteConfigurationFunctions.Add(host =>
            {
                var ret = new MessageRouteConfiguration<TMessage>(addressMap, host);
                if (config != null)
                    ret = config.Invoke(ret);
                return ret;
            })
        };

    public RoutedHubConfiguration RouteAddress<TAddress>(Func<IMessageDelivery, object> addressMap, Func<AddressRouteConfiguration<TAddress>, AddressRouteConfiguration<TAddress>> config = null)
        => this with
        {
            MessageRouteConfigurationFunctions = MessageRouteConfigurationFunctions.Add(host =>
            {
                var ret = new AddressRouteConfiguration<TAddress>(addressMap, host);
                if (config != null)
                    ret = config.Invoke(ret);
                return ret;
            })
        };

    public RoutedHubConfiguration ForwardToTarget<TMessage>(object address, Func<ForwardConfigurationItem<TMessage>, ForwardConfigurationItem<TMessage>> config = null)
    {
        return this with
        {
            ForwardConfigurationFunctions = ForwardConfigurationFunctions.Add(host =>
            {
                var ret = new ForwardConfigurationItem<TMessage>();
                if (config != null)
                    ret = config.Invoke(ret);
                return ret;
            })
        };
    }

    public RoutedHubConfiguration Buildup(IMessageHub host) => this with
    {
        MessageRoutes = MessageRouteConfigurationFunctions.Select(c => c.Invoke(host)).ToImmutableList(),
        ForwardConfigurationItems = ForwardConfigurationFunctions.Select(c => c.Invoke(host)).ToImmutableList(),
    };
}

public abstract record MessageRouteConfiguration(Func<IMessageDelivery, object> AddressMap, IMessageHub Host) : IForwardConfigurationItem
{
    internal const string MaskedRequest = nameof(MaskedRequest);
    AsyncDelivery IForwardConfigurationItem.Route => d => Task.FromResult(Route(d));

    bool IForwardConfigurationItem.Filter(IMessageDelivery delivery) => Applies(delivery);
    protected abstract bool Applies(IMessageDelivery delivery);

    private IMessageDelivery Route(IMessageDelivery delivery)
    {
        var posted = Host.Post(delivery.Message, d => d.WithTarget(AddressMap.Invoke(delivery)).WithProperties(d.Properties));

        if (delivery.Message is IRequest)
            Host.RegisterCallback(posted, r => HandleWayBack(delivery, r));

        return delivery.Forwarded();
    }

    private IMessageDelivery HandleWayBack(IMessageDelivery request, IMessageDelivery response)
    {
        Host.Post(response.Message, o => o.WithTarget(request.Sender).WithProperties(response.Properties).WithRequestIdFrom(request));
        return request.Processed();
    }
}

public record AddressRouteConfiguration<TAddress>(Func<IMessageDelivery, object> AddressMap, IMessageHub Host) : MessageRouteConfiguration(AddressMap, Host)
{
    public AddressRouteConfiguration<TAddress> WithFilter(Func<IMessageDelivery, bool> filter) => this with { Filter = filter };
    internal Func<IMessageDelivery, bool> Filter { get; init; } = _ => true;
    protected bool AddressFilter(IMessageDelivery delivery) => delivery.Target.IsAddress<TAddress>();

    protected override bool Applies(IMessageDelivery delivery) => AddressFilter(delivery) && Filter(delivery);
}

public record MessageRouteConfiguration<TMessage>(Func<IMessageDelivery, object> AddressMap, IMessageHub Host) : MessageRouteConfiguration(AddressMap, Host)
{
    public MessageRouteConfiguration<TMessage> WithFilter(Func<IMessageDelivery<TMessage>, bool> filter) => this with { Filter = filter };
    internal Func<IMessageDelivery<TMessage>, bool> Filter { get; init; } = _ => true;

    protected bool AddressFilter(IMessageDelivery delivery) => (delivery.Target == null || delivery.Target.Equals(Host.Address));
    protected override bool Applies(IMessageDelivery delivery) => AddressFilter(delivery) && delivery is IMessageDelivery<TMessage> typedDelivery && Filter(typedDelivery);
}

internal record MessageHandlerItem(Type MessageType, AsyncDelivery Action, DeliveryFilter Filter);
