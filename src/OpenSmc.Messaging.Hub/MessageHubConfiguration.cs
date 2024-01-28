using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenSmc.Serialization;
using OpenSmc.ServiceProvider;
using OpenSmc.ShortGuid;

namespace OpenSmc.Messaging;

public static class MessageHubExtensions
{
    public static IMessageHub<TAddress> CreateMessageHub<TAddress>(this IServiceProvider serviceProvider, TAddress address, Func<MessageHubConfiguration, MessageHubConfiguration> configuration)
    {
        var hubSetup = new MessageHubConfiguration(serviceProvider, address);
        return (IMessageHub<TAddress>)configuration(hubSetup).Build(serviceProvider, address);
    }
}

public record MessageHubConfiguration
{
    public object Address { get; }
    protected readonly IServiceProvider ParentServiceProvider;
    public MessageHubConfiguration(IServiceProvider parentServiceProvider, object address)
    {
        Address = address;
        ParentServiceProvider = parentServiceProvider;
    }

    internal Func<IServiceCollection, IServiceCollection> Services { get; init; } = x => x;

    public IServiceProvider ServiceProvider { get; set; }

    internal ImmutableList<Func<IMessageHub, Task>> DisposeActions { get; init; } = ImmutableList<Func<IMessageHub, Task>>.Empty;

    internal IMessageHub ParentHub { get; init; }
    internal object SynchronizationAddress { get; init; }

    internal ImmutableList<MessageHandlerItem> MessageHandlers { get; init; } = ImmutableList<MessageHandlerItem>.Empty;
    internal Func<string> GetAccessObject { get; init; }
    internal ImmutableList<StartConfiguration> StartConfigurations { get; init; } = ImmutableList<StartConfiguration>.Empty;

    protected internal ImmutableList<Func<IMessageHub, Task>> BuildupActions { get; init; } = ImmutableList<Func<IMessageHub, Task>>.Empty;

    internal ImmutableList<DeliveryFilter> Deferrals { get; init; } = ImmutableList<DeliveryFilter>.Empty;

    internal IMessageHub HubInstance { get; set; }

    public MessageHubConfiguration WithDisposeAction(Action<IMessageHub> disposeAction)
        => WithDisposeAction(m =>
        {
            disposeAction.Invoke(m);
            return Task.CompletedTask;
        });
    public MessageHubConfiguration WithDisposeAction(Func<IMessageHub, Task> disposeAction) => this with { DisposeActions = DisposeActions.Add(disposeAction) };

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


    public MessageHubConfiguration WithAccessObject(Func<string> getAccessObject)
    {
        return this with { GetAccessObject = getAccessObject };
    }


    protected virtual ServiceCollection ConfigureServices<TAddress>(IMessageHub parent)
    {
        var services = new ServiceCollection();
        services.Replace(ServiceDescriptor.Singleton<IMessageHub>(sp => new MessageHub<TAddress>(sp, sp.GetRequiredService<HostedHubsCollection>(), this, parent)));
        services.Replace(ServiceDescriptor.Singleton<HostedHubsCollection, HostedHubsCollection>());
        services.Replace(ServiceDescriptor.Singleton(typeof(IEventsRegistry),
            sp => new EventsRegistry(ParentServiceProvider.GetService<IEventsRegistry>())));
        services.Replace(ServiceDescriptor.Singleton<IMessageService>(sp => new MessageService(Address,
            sp.GetService<ISerializationService>(), // HACK: GetRequiredService replaced by GetService (16.01.2024, Alexander Yolokhov)
            sp.GetRequiredService<ILogger<MessageService>>()
        )));
        services.Replace(ServiceDescriptor.Singleton(sp => new ParentMessageHub(sp.GetRequiredService<IMessageHub>())));
        Services.Invoke(services);
        return services;
    }

    private record ParentMessageHub(IMessageHub Value);

    public MessageHubConfiguration WithHandler<TMessage>(Func<IMessageHub, IMessageDelivery<TMessage>, IMessageDelivery> delivery) => WithHandler<TMessage>((h,d) => Task.FromResult(delivery.Invoke(h, d)));
    public MessageHubConfiguration WithHandler<TMessage>(Func<IMessageHub, IMessageDelivery<TMessage>, Task<IMessageDelivery>> delivery) => this with {MessageHandlers = MessageHandlers.Add(new(typeof(TMessage), (h,m) => delivery.Invoke(h,(IMessageDelivery<TMessage>)m)))};


    public MessageHubConfiguration WithBuildupAction(Action<IMessageHub> action) => this with { BuildupActions = BuildupActions.Add(hub => { action(hub); return Task.CompletedTask; }) };
    public MessageHubConfiguration WithBuildupAction(Func<IMessageHub, Task> action) => this with { BuildupActions = BuildupActions.Add(action) };


    public MessageHubConfiguration WithDeferral(DeliveryFilter deferral)
        => this with { Deferrals = Deferrals.Add(deferral) };

    protected void CreateServiceProvider<TAddress>(IMessageHub parent)
    {

        ServiceProvider = ConfigureServices<TAddress>(parent)
            .SetupModules(ParentServiceProvider, new ModulesBuilder().Add(GetType().Assembly), Guid.NewGuid().AsString());
    }

    public virtual IMessageHub Build<TAddress>(IServiceProvider serviceProvider, TAddress address)
    {
        // TODO V10: Check whether this address is already built in hosted hubs collection, if not build. (18.01.2024, Roland Buergi)
        var parentHub = ParentServiceProvider.GetService<ParentMessageHub>()?.Value;
        CreateServiceProvider<TAddress>(parentHub);
        

        HubInstance = ServiceProvider.GetRequiredService<IMessageHub>();

        ServiceProvider.GetService<IMessageService>().Start();
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
    internal ImmutableList<IForwardConfigurationItem> ForwardConfigurationItems { get; init; }
    internal ImmutableList<Func<IMessageHub, IForwardConfigurationItem>> ForwardConfigurationFunctions = ImmutableList<Func<IMessageHub, IForwardConfigurationItem>>.Empty;


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


public record MessageRouteConfiguration<TMessage>(Func<IMessageDelivery, object> AddressMap, IMessageHub Host) : MessageRouteConfiguration(AddressMap, Host)
{
    public MessageRouteConfiguration<TMessage> WithFilter(Func<IMessageDelivery<TMessage>, bool> filter) => this with { Filter = filter };
    internal Func<IMessageDelivery<TMessage>, bool> Filter { get; init; } = _ => true;

    protected bool AddressFilter(IMessageDelivery delivery) => (delivery.Target == null || delivery.Target.Equals(Host.Address));
    protected override bool Applies(IMessageDelivery delivery) => AddressFilter(delivery) && delivery is IMessageDelivery<TMessage> typedDelivery && Filter(typedDelivery);
}

internal record MessageHandlerItem(Type MessageType, Func<IMessageHub, IMessageDelivery, Task<IMessageDelivery>> AsyncDelivery);
