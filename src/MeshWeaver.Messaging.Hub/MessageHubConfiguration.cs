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
    protected readonly IServiceProvider? ParentServiceProvider;
    public MessageHubConfiguration(IServiceProvider? parentServiceProvider, Address address)
    {
        Address = address;
        ParentServiceProvider = parentServiceProvider;
        TypeRegistry = new TypeRegistry(ParentServiceProvider?.GetService<ITypeRegistry>()).WithType(address.GetType());
        PostPipeline = [UserServicePostPipeline];
        DeliveryPipeline = [UserServiceDeliveryPipeline];
    }

    public IMessageHub? ParentHub => ParentServiceProvider?.GetService<IMessageHub>();
    internal Func<IServiceCollection, IServiceCollection> Services { get; init; } = x => x;

    public IServiceProvider ServiceProvider { get; set; } = null!;
    private readonly Lock serviceProviderLock = new();

    internal ImmutableList<Func<IMessageHub, CancellationToken, Task>> DisposeActions { get; init; } = [];

    internal ImmutableList<MessageHandlerItem> MessageHandlers { get; init; } = ImmutableList<MessageHandlerItem>.Empty;

    protected internal ImmutableList<Func<IMessageHub, CancellationToken, Task>> BuildupActions { get; init; } = ImmutableList<Func<IMessageHub, CancellationToken, Task>>.Empty;
    protected internal ImmutableList<Action<IMessageHub>> SyncBuildupActions { get; init; } = [];


    internal IMessageHub HubInstance { get; set; } = null!;

    public MessageHubConfiguration RegisterForDisposal(Action<IMessageHub> disposeAction)
        => RegisterForDisposal((m, _) =>
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
                .WithRoutes(f => f.RouteAddress<Address>((a, d) =>
        {
            if (!address.Equals(a))
                return d;
            var hub = f.Hub.GetHostedHub(a, configuration)?.DeliverMessage(d);

            if (hub is null)
                throw new ArgumentException($"Could not find hub with address {a}");
            return d.Forwarded();
        }));


    public ITypeRegistry TypeRegistry { get; }
    protected virtual ServiceCollection ConfigureServices(IMessageHub? parent)
    {
        var services = new ServiceCollection();
        services.Replace(ServiceDescriptor.Singleton<IMessageHub>(sp => new MessageHub(sp, sp.GetRequiredService<HostedHubsCollection>(), this, parent)));
        services.Replace(ServiceDescriptor.Singleton<HostedHubsCollection, HostedHubsCollection>(sp => new(sp, Address)));
        services.Replace(ServiceDescriptor.Singleton(typeof(ITypeRegistry), _ => TypeRegistry));
        services.Replace(ServiceDescriptor.Singleton(sp => new ParentMessageHub(sp.GetRequiredService<IMessageHub>())));
        // Check if AccessService is registered in the parent service provider
        if (ParentServiceProvider?.GetService<AccessService>() == null)
        {
            services.AddSingleton<AccessService>();
        }
        Services.Invoke(services);
        return services;
    }



    private record ParentMessageHub(IMessageHub Value);


    public MessageHubConfiguration WithHandler<TMessage>(Func<IMessageHub, IMessageDelivery<TMessage>, IMessageDelivery> delivery, Func<IMessageHub, IMessageDelivery, bool>? filter = null) =>
        WithHandler<TMessage>((h, d, _) => Task.FromResult(delivery.Invoke(h, d)), filter);
    public MessageHubConfiguration WithHandler<TMessage>(Func<IMessageHub, IMessageDelivery<TMessage>, CancellationToken, Task<IMessageDelivery>> delivery, Func<IMessageHub, IMessageDelivery, bool>? filter = null)
    {
        TypeRegistry.GetOrAddType(typeof(TMessage));
        return this with
        {
            MessageHandlers = MessageHandlers.Add(
                new(typeof(TMessage),
                    (h, m, c) =>
                        m is IMessageDelivery<TMessage> mdTyped &&
                        (filter ?? DefaultFilter).Invoke(h, m)
                            ? delivery.Invoke(h, mdTyped, c)
                            : Task.FromResult(m)))
        };
    }

    private static bool DefaultFilter(IMessageHub hub, IMessageDelivery delivery) => delivery.Target == null || delivery.Target.Equals(hub.Address);

    public MessageHubConfiguration WithInitialization(Action<IMessageHub> action) => this with
    {
        SyncBuildupActions = SyncBuildupActions.Add(action)
    };
    public MessageHubConfiguration WithInitialization(Func<IMessageHub, CancellationToken, Task> action) => this with { BuildupActions = BuildupActions.Add(action) };



    protected void CreateServiceProvider(IMessageHub? parent)
    {
        lock (serviceProviderLock)
        {
            if (ServiceProvider != null!)
                return; // Already created
                
            ServiceProvider = ConfigureServices(parent)
                .SetupModules(ParentServiceProvider);
        }
    }

    public virtual IMessageHub Build<TAddress>(IServiceProvider serviceProvider, TAddress address)
    {
        // TODO V10: Check whether this address is already built in hosted hubs collection, if not build. (18.01.2024, Roland Buergi)
        var parentHub = ParentServiceProvider?.GetService<ParentMessageHub>()?.Value;
        CreateServiceProvider(parentHub);
        var parentHubs = ParentServiceProvider?.GetService<HostedHubsCollection>();

        HubInstance = ServiceProvider.GetRequiredService<IMessageHub>();
        parentHubs?.Add(HubInstance);

        // Execute synchronous initialization actions immediately after hub creation
        foreach (var initAction in SyncBuildupActions)
            initAction(HubInstance);

        return HubInstance;
    }



    internal ImmutableDictionary<(Type, string?), object> Properties { get; init; } = ImmutableDictionary<(Type, string?), object>.Empty;
    internal ImmutableList<Func<SyncPipelineConfig, SyncPipelineConfig>> PostPipeline { get; set; }

    public MessageHubConfiguration AddPostPipeline(Func<SyncPipelineConfig, SyncPipelineConfig> pipeline) => this with { PostPipeline = PostPipeline.Add(pipeline) };
    private SyncPipelineConfig UserServicePostPipeline(SyncPipelineConfig syncPipeline)
    {
        var userService = syncPipeline.Hub.ServiceProvider.GetService<AccessService>();
        return syncPipeline.AddPipeline((d, next) =>
        {
            var context = userService?.Context;
            if (context is not null)
                d = d.SetAccessContext(context);
            return next(d);

        });
    }
    internal ImmutableList<Func<AsyncPipelineConfig, AsyncPipelineConfig>> DeliveryPipeline { get; set; }
    internal long StartupTimeout { get; init; }

    public MessageHubConfiguration WithStartupTimeout(long timeout) => this with { StartupTimeout = timeout };

    public MessageHubConfiguration AddDeliveryPipeline(Func<AsyncPipelineConfig, AsyncPipelineConfig> pipeline) => this with { DeliveryPipeline = DeliveryPipeline.Add(pipeline) };
    private AsyncPipelineConfig UserServiceDeliveryPipeline(AsyncPipelineConfig asyncPipeline)
    {
        var userService = asyncPipeline.Hub.ServiceProvider.GetService<AccessService>();
        return asyncPipeline.AddPipeline(async (d, ct, next) =>
        {
            userService?.SetContext(d.AccessContext);
            try
            {
                return await next.Invoke(d, ct);
            }
            finally
            {
                userService?.SetContext(null);
            }
        });
    }

    public T? Get<T>(string? context = null) => (T?)(Properties.GetValueOrDefault((typeof(T), context)) ?? default(T));
    public MessageHubConfiguration Set<T>(T value, string? context = null) => this with { Properties = Properties.SetItem((typeof(T), context), value!) };


    public MessageHubConfiguration WithType<T>(string? name = null)
    {
        TypeRegistry.WithType(typeof(T), name ?? typeof(T).Name);
        return this;
    }
    public MessageHubConfiguration WithType(Type type, string? name = null)
    {
        TypeRegistry.WithType(type, name ?? type.Name);
        return this;
    }
}

public record AsyncPipelineConfig
{
    public AsyncPipelineConfig(IMessageHub Hub, AsyncDelivery asyncDelivery)
    {
        this.Hub = Hub;
        AsyncDelivery = asyncDelivery;

    }

    internal AsyncDelivery AsyncDelivery { get; init; }

    public AsyncPipelineConfig AddPipeline(
        Func<IMessageDelivery, CancellationToken, AsyncDelivery, Task<IMessageDelivery>> pipeline)
        => this with { AsyncDelivery = (d, ct) => pipeline.Invoke(d, ct, AsyncDelivery) };

    public IMessageHub Hub { get; init; }
}
public record SyncPipelineConfig
{
    public SyncPipelineConfig(IMessageHub Hub, SyncDelivery syncDelivery)
    {
        this.Hub = Hub;
        SyncDelivery = syncDelivery;

    }

    internal SyncDelivery SyncDelivery { get; init; }

    public SyncPipelineConfig AddPipeline(
        Func<IMessageDelivery, SyncDelivery, IMessageDelivery> pipeline)
        => this with { SyncDelivery = d => pipeline.Invoke(d, SyncDelivery) };

    public IMessageHub Hub { get; init; }
}

internal record MessageHandlerItem(Type MessageType, Func<IMessageHub, IMessageDelivery, CancellationToken, Task<IMessageDelivery>> AsyncDelivery);
