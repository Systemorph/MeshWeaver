using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.ServiceProvider;

namespace MeshWeaver.Messaging;

public static class MessageHubExtensions
{
    public static IMessageHub CreateMessageHub(this IServiceProvider serviceProvider, object address, Func<MessageHubConfiguration, MessageHubConfiguration> configuration)
    {
        var hubSetup = new MessageHubConfiguration(serviceProvider, address);
        return configuration(hubSetup).Build(serviceProvider, address);
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

    protected internal ImmutableList<Func<IMessageHub, CancellationToken, Task>> BuildupActions { get; init; } = ImmutableList<Func<IMessageHub, CancellationToken, Task>>.Empty;

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


    internal Func<RouteConfiguration, RouteConfiguration> ForwardConfigurationBuilder { get; init; }
    internal ImmutableList<(Type Type,Func<IMessageHub,IMessageHubPlugin> Factory)> PluginFactories { get; init; } = ImmutableList<(Type Type, Func<IMessageHub, IMessageHubPlugin> Factory)>.Empty;


    public MessageHubConfiguration WithServices(Func<IServiceCollection, IServiceCollection> configuration)
    {
        return this with { Services = x => configuration(Services(x)) };
    }
    public MessageHubConfiguration SynchronizeFrom(object address) => this with { SynchronizationAddress = address };


    public MessageHubConfiguration WithRoutes(Func<RouteConfiguration, RouteConfiguration> configuration) => this with { ForwardConfigurationBuilder = x => configuration(ForwardConfigurationBuilder?.Invoke(x) ?? x) };


    public MessageHubConfiguration WithAccessObject(Func<string> getAccessObject)
    {
        return this with { GetAccessObject = getAccessObject };
    }


    public MessageHubConfiguration WithHostedHub(object address,
        Func<MessageHubConfiguration, MessageHubConfiguration> configuration)
        => WithRoutes(f => f.RouteAddress<object>((a, d) =>
        {
            if (!address.Equals(a))
                return d;
            f.Hub.GetHostedHub(a, configuration).DeliverMessage(d);
            return d.Forwarded();
        }));


    protected virtual ServiceCollection ConfigureServices(IMessageHub parent)
    {
        var services = new ServiceCollection();
        services.Replace(ServiceDescriptor.Singleton<IMessageHub>(sp => new MessageHub(sp, sp.GetRequiredService<HostedHubsCollection>(), this, parent)));
        services.Replace(ServiceDescriptor.Singleton<HostedHubsCollection, HostedHubsCollection>());
        services.Replace(ServiceDescriptor.Singleton(typeof(ITypeRegistry),
            _ => new TypeRegistry(ParentServiceProvider.GetService<ITypeRegistry>())));
        services.Replace(ServiceDescriptor.Singleton<IMessageService>(sp => new MessageService(Address,sp.GetRequiredService<ILogger<MessageService>>())));
        services.Replace(ServiceDescriptor.Singleton(sp => new ParentMessageHub(sp.GetRequiredService<IMessageHub>())));
        Services.Invoke(services);
        return services;
    }

    private record ParentMessageHub(IMessageHub Value);

    public MessageHubConfiguration WithHandler<TMessage>(Func<IMessageHub, IMessageDelivery<TMessage>, IMessageDelivery> delivery) => WithHandler<TMessage>((h,d,_) => Task.FromResult(delivery.Invoke(h, d)));
    public MessageHubConfiguration WithHandler<TMessage>(Func<IMessageHub, IMessageDelivery<TMessage>, CancellationToken, Task<IMessageDelivery>> delivery) => this with {MessageHandlers = MessageHandlers.Add(new(typeof(TMessage), (h,m,c) => delivery.Invoke(h,(IMessageDelivery<TMessage>)m,c)))};


    public MessageHubConfiguration WithInitialization(Action<IMessageHub> action) => this with { BuildupActions = BuildupActions.Add((hub,_) => { action(hub); return Task.CompletedTask; }) };
    public MessageHubConfiguration WithBuildupAction(Func<IMessageHub, CancellationToken, Task> action) => this with { BuildupActions = BuildupActions.Add(action) };


    public MessageHubConfiguration WithDeferral(DeliveryFilter deferral)
        => this with { Deferrals = Deferrals.Add(deferral) };

    protected void CreateServiceProvider(IMessageHub parent)
    {

        ServiceProvider = ConfigureServices(parent)
            .SetupModules(ParentServiceProvider);
    }

    public virtual IMessageHub Build<TAddress>(IServiceProvider serviceProvider, TAddress address)
    {
        // TODO V10: Check whether this address is already built in hosted hubs collection, if not build. (18.01.2024, Roland Buergi)
        var parentHub = ParentServiceProvider.GetService<ParentMessageHub>()?.Value;
        CreateServiceProvider(parentHub);
        var parentHubs = ParentServiceProvider.GetService<HostedHubsCollection>();

        HubInstance = ServiceProvider.GetRequiredService<IMessageHub>();
        if(parentHubs != null)
            parentHubs.Add(HubInstance);
        return HubInstance;
    }



    internal ImmutableDictionary<Type, object> Properties { get; init; } = ImmutableDictionary<Type, object>.Empty;
    public T Get<T>() => (T)(Properties.GetValueOrDefault(typeof(T)) ?? default(T));
    public MessageHubConfiguration Set<T>(T value) => this with { Properties = Properties.SetItem(typeof(T), value) };

}


internal record MessageHandlerItem(Type MessageType, Func<IMessageHub, IMessageDelivery, CancellationToken, Task<IMessageDelivery>> AsyncDelivery);
