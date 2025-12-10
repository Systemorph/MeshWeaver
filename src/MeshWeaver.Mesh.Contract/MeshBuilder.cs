using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

[assembly: InternalsVisibleTo("MeshWeaver.Hosting")]
namespace MeshWeaver.Mesh;

public record MeshBuilder
{
    public MeshBuilder(Action<Func<IServiceCollection, IServiceCollection>> ServiceConfig, Address Address)
    {
        this.ServiceConfig = ServiceConfig;
        this.Address = Address;
        Register();
    }

    private List<MeshNode> MeshNodes { get; } = new();
    private readonly UnifiedPathRegistry pathRegistry = new();

    public MeshBuilder InstallAssemblies(params string[] assemblyLocations)
    {
        var attributes = assemblyLocations
            .Select(Assembly.LoadFrom)
            .SelectMany(a => a.GetCustomAttributes<MeshNodeAttribute>())
            .ToArray();
        MeshNodes.AddRange(attributes.SelectMany(a => InstallServices(a.Nodes)));

        // Install node factories from attributes
        foreach (var factory in attributes.SelectMany(a => a.NodeFactories))
        {
            AddMeshNodeFactory(factory);
        }

        // Install namespaces from attributes (for autocomplete)
        foreach (var ns in attributes.SelectMany(a => a.Namespaces))
        {
            AddMeshNamespace(ns);
        }

        // Register address types from attributes
        var addressTypes = attributes.SelectMany(a => a.AddressTypes).ToArray();
        if (addressTypes.Length > 0)
        {
            ConfigureHub(config =>
            {
                config.TypeRegistry.WithTypes(addressTypes);
                return config;
            });
        }

        // Register path prefixes from attributes
        foreach (var prefix in attributes.SelectMany(a => a.PathPrefixes))
        {
            pathRegistry.Register(prefix.Key, prefix.Value);
        }

        return this;
    }

    private IEnumerable<MeshNode> InstallServices(IEnumerable<MeshNode> nodes)
    {
        foreach (var meshNode in nodes)
        {
            foreach (var config in meshNode.GlobalServiceConfigurations)
            {
                ConfigureServices(config);
            }
            yield return meshNode;
        }
    }


    private List<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfiguration { get; } = new()    {
        AddMesh
    };
    public MeshBuilder ConfigureHub(
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration)
    {
        HubConfiguration.Add(hubConfiguration);
        return this;
    }

    private List<Func<MeshConfiguration, MeshConfiguration>> MeshConfiguration { get; } = new();


    public MeshBuilder ConfigureServices(Func<IServiceCollection, IServiceCollection> configuration)
    {
        ServiceConfig.Invoke(configuration);
        return this;
    }
    private Action<Func<IServiceCollection, IServiceCollection>> ServiceConfig { get; init; }
    public Address Address { get; init; }


    public MeshBuilder ConfigureMesh(Func<MeshConfiguration, MeshConfiguration> configuration)
    {
        MeshConfiguration.Add(configuration);
        return this;
    }

    private void Register()
    {
        // Register built-in path handlers
        pathRegistry.Register("data", new DataPathHandler());
        pathRegistry.Register("area", new AreaPathHandler());
        pathRegistry.Register("content", new ContentPathHandler());

        ConfigureServices(services => services
            .AddSingleton(_ => new MeshConfiguration(MeshNodes.ToDictionary(x => x.Name), factories, namespaces))
            .AddSingleton<IUnifiedPathRegistry>(_ => pathRegistry)
            .AddSingleton(BuildHub)
            .AddSingleton<AccessService>()
            );

        IReadOnlyCollection<Func<MeshConfiguration, MeshConfiguration>> meshConfig = MeshConfiguration;

        ConfigureHub(conf => conf.WithRoutes(routes =>
                routes.WithHandler((delivery, ct) =>
                {
                    if (delivery.State != MessageDeliveryState.Submitted || delivery.Target == null || delivery.Target.Equals(Address))
                        return Task.FromResult(delivery);

                    return routes.Hub.ServiceProvider.GetRequiredService<IRoutingService>().DeliverMessageAsync(delivery.Package(), ct);
                }))
            .Set(meshConfig)
        );

    }

    public virtual IMessageHub BuildHub(IServiceProvider sp)
    {
        return sp.CreateMessageHub(Address, conf => HubConfiguration.Aggregate(conf, (x, y) => y.Invoke(x)));
    }
    private static MessageHubConfiguration AddMesh(MessageHubConfiguration configuration)
    {
        return configuration
            .AddMeshTypes();
    }
    public MeshBuilder AddMeshNodes(params IEnumerable<MeshNode> nodes)
    {
        MeshNodes.AddRange(nodes);
        return this;
    }

    private readonly List<Func<Address, MeshNode?>> factories = new();
    private readonly List<MeshNamespace> namespaces = new();

    public MeshBuilder AddMeshNodeFactory(Func<Address, MeshNode?> meshNodeFactory)
    {
        factories.Add(meshNodeFactory);
        return this;
    }

    /// <summary>
    /// Adds a namespace that describes an address type for autocomplete.
    /// The namespace provides metadata (description, icon) and optionally a factory function.
    /// </summary>
    public MeshBuilder AddMeshNamespace(MeshNamespace ns)
    {
        namespaces.Add(ns);
        // If the namespace has a factory function, also register it
        if (ns.Factory != null)
        {
            factories.Add(ns.Factory);
        }
        return this;
    }
}
