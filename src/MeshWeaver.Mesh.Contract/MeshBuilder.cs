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
    private List<NodeTypeConfiguration> NodeTypeConfigs { get; } = new();
    private readonly UnifiedPathRegistry pathRegistry = new();

    public MeshBuilder InstallAssemblies(params string[] assemblyLocations)
    {
        var attributes = assemblyLocations
            .Select(Assembly.LoadFrom)
            .SelectMany(a => a.GetCustomAttributes<MeshNodeAttribute>())
            .ToArray();
        MeshNodes.AddRange(attributes.SelectMany(a => InstallServices(a.Nodes)));

        // Collect node type configurations from attributes
        var nodeTypeConfigs = attributes.SelectMany(a => a.NodeTypeConfigurations).ToArray();
        NodeTypeConfigs.AddRange(nodeTypeConfigs);

        // Register DataTypes from node type configurations for serialization
        // Use short names (Type.Name) for consistency with TypeSource registrations
        var dataTypes = nodeTypeConfigs
            .Select(c => c.DataType)
            .Where(t => t != null && t != typeof(object))
            .Distinct()
            .ToArray();
        if (dataTypes.Length > 0)
        {
            ConfigureHub(config =>
            {
                foreach (var dataType in dataTypes)
                    config.TypeRegistry.WithType(dataType, dataType.Name);
                return config;
            });
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
            .AddSingleton(_ => new MeshConfiguration(
                MeshNodes.ToDictionary(x => x.Key),
                NodeTypeConfigs.ToDictionary(x => x.NodeType)))
            .AddSingleton<IUnifiedPathRegistry>(_ => pathRegistry)
            .AddSingleton(BuildHub)
            .AddSingleton<AccessService>()
            );

        IReadOnlyCollection<Func<MeshConfiguration, MeshConfiguration>> meshConfig = MeshConfiguration;

        ConfigureHub(conf => conf.WithRoutes(routes =>
                routes.WithHandler((delivery, ct) =>
                {
                    // Compare without Host since Host tracks routing path
                    var targetWithoutHost = delivery.Target is not null ? delivery.Target with { Host = null } : null;
                    if (delivery.State != MessageDeliveryState.Submitted || targetWithoutHost == null || targetWithoutHost.Equals(Address))
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
}
