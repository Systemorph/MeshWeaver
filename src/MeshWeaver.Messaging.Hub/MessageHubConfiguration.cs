using System.Collections.Immutable;
using MeshWeaver.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.ServiceProvider;

namespace MeshWeaver.Messaging;

public record MessageHubConfiguration
{
    public Address Address { get; }
    protected readonly IServiceProvider ParentServiceProvider;
    public MessageHubConfiguration(IServiceProvider parentServiceProvider, Address address)
    {
        Address = address;
        ParentServiceProvider = parentServiceProvider;
        TypeRegistry  = new TypeRegistry(ParentServiceProvider?.GetService<ITypeRegistry>()).WithType(address.GetType());
    }

    internal Func<IServiceCollection, IServiceCollection> Services { get; init; } = x => x;

    public IServiceProvider ServiceProvider { get; set; }

    internal ImmutableList<Func<IMessageHub, CancellationToken, Task>> DisposeActions { get; init; } = [];

    internal ImmutableList<MessageHandlerItem> MessageHandlers { get; init; } = ImmutableList<MessageHandlerItem>.Empty;

    protected internal ImmutableList<Func<IMessageHub, CancellationToken, Task>> BuildupActions { get; init; } = ImmutableList<Func<IMessageHub, CancellationToken, Task>>.Empty;


    internal IMessageHub HubInstance { get; set; }

    public MessageHubConfiguration RegisterForDisposal(Action<IMessageHub> disposeAction)
        => RegisterForDisposal((m,_) =>
        {
            disposeAction.Invoke(m);
            return Task.CompletedTask;
        });
    public MessageHubConfiguration RegisterForDisposal(Func<IMessageHub, CancellationToken, Task> disposeAction) => this with { DisposeActions = DisposeActions.Add(disposeAction) };





    public MessageHubConfiguration WithServices(Func<IServiceCollection, IServiceCollection> configuration)
    {
        return this with { Services = x => configuration(Services(x)) };
    }

    public MessageHubConfiguration WithHostedHub(Address address,
        Func<MessageHubConfiguration, MessageHubConfiguration> configuration)
        =>
            this.WithTypes(address.GetType())
                .WithRoutes(f => f.RouteAddress<Address>(async (a, d,ct) =>
        {
            if (!address.Equals(a))
                return d;
            await f.Hub.GetHostedHub(a, configuration).DeliverMessageAsync(d, ct);
            return d.Forwarded();
        }));


    public ITypeRegistry TypeRegistry { get; }
    protected virtual ServiceCollection ConfigureServices(IMessageHub parent)
    {
        var services = new ServiceCollection();
        services.Replace(ServiceDescriptor.Singleton<IMessageHub>(sp => new MessageHub(sp, sp.GetRequiredService<HostedHubsCollection>(), this, parent)));
        services.Replace(ServiceDescriptor.Singleton<HostedHubsCollection, HostedHubsCollection>());
        services.Replace(ServiceDescriptor.Singleton(typeof(ITypeRegistry), _ => TypeRegistry));
        services.Replace(ServiceDescriptor.Singleton(sp => new ParentMessageHub(sp.GetRequiredService<IMessageHub>())));
        Services.Invoke(services);
        return services;
    }



    private record ParentMessageHub(IMessageHub Value);


    public MessageHubConfiguration WithHandler<TMessage>(Func<IMessageHub, IMessageDelivery<TMessage>, IMessageDelivery> delivery, Func<IMessageHub,IMessageDelivery, bool> filter = null) => 
        WithHandler<TMessage>((h,d,_) => Task.FromResult(delivery.Invoke(h, d)), filter);
    public MessageHubConfiguration WithHandler<TMessage>(Func<IMessageHub, IMessageDelivery<TMessage>, CancellationToken, Task<IMessageDelivery>> delivery, Func<IMessageHub, IMessageDelivery, bool> filter = null) => 
        this with
        {
            MessageHandlers = MessageHandlers.Add(
                new(typeof(TMessage), 
                (h,m,c) => 
                    m is IMessageDelivery<TMessage> mdTyped &&
                        (filter ?? DefaultFilter).Invoke(h, m) ?
                        delivery.Invoke(h,mdTyped,c)
                : Task.FromResult(m)))
        };

    private static bool DefaultFilter(IMessageHub hub, IMessageDelivery delivery) => delivery.Target == null || delivery.Target.Equals(hub.Address);

    public MessageHubConfiguration WithInitialization(Action<IMessageHub> action) => this with
    {
        BuildupActions = BuildupActions.Add((hub, _) =>
        {
            action(hub); return Task.CompletedTask; 
        })
    };
    public MessageHubConfiguration WithInitialization(Func<IMessageHub, CancellationToken, Task> action) => this with { BuildupActions = BuildupActions.Add(action) };



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
