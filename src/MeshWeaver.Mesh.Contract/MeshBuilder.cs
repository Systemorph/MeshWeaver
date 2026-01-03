using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Domain;
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

    public MeshBuilder InstallAssemblies(params string[] assemblyLocations)
    {
        var attributes = assemblyLocations
            .Select(Assembly.LoadFrom)
            .SelectMany(a => a.GetCustomAttributes<MeshNodeAttribute>())
            .ToArray();
        MeshNodes.AddRange(attributes.SelectMany(a => InstallServices(a.Nodes)));

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


    private List<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfiguration { get; } = [AddMesh];
    public MeshBuilder ConfigureHub(
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration)
    {
        HubConfiguration.Add(hubConfiguration);
        return this;
    }

    private List<Func<MeshConfiguration, MeshConfiguration>> MeshConfiguration { get; } = new();

    private List<Func<MessageHubConfiguration, MessageHubConfiguration>> DefaultNodeHubConfiguration { get; } = new();

    /// <summary>
    /// Configures the default hub configuration that will be applied to all node hubs.
    /// Use this for settings like content collections (e.g., logos) that should be available everywhere.
    /// </summary>
    public MeshBuilder ConfigureDefaultNodeHub(
        Func<MessageHubConfiguration, MessageHubConfiguration> configuration)
    {
        DefaultNodeHubConfiguration.Add(configuration);
        return this;
    }

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
        // Create mesh-level type registry for polymorphic serialization
        // Hub-level type registries will inherit from this via ParentServiceProvider
        var meshTypeRegistry = MessageHubExtensions.CreateTypeRegistry();

        // Capture the list reference - will be populated by ConfigureDefaultNodeHub calls later
        // The lambda is evaluated when MeshConfiguration is resolved (after all builder calls)
        var defaultNodeHubConfigs = DefaultNodeHubConfiguration;

        ConfigureServices(services => services
            .AddSingleton(_ =>
            {
                // Evaluate defaultNodeHubConfigs at service resolution time, not at Register() time
                Func<MessageHubConfiguration, MessageHubConfiguration>? combinedDefaultConfig =
                    defaultNodeHubConfigs.Count > 0
                        ? config => defaultNodeHubConfigs.Aggregate(config, (c, f) => f(c))
                        : null;
                return new MeshConfiguration(MeshNodes.ToDictionary(x => x.Path), combinedDefaultConfig);
            })
            .AddSingleton<ITypeRegistry>(_ => meshTypeRegistry)
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
            .AddMeshTypes()
            .WithNodeOperationHandlers();
    }
    public MeshBuilder AddMeshNodes(params IEnumerable<MeshNode> nodes)
    {
        MeshNodes.AddRange(nodes);
        return this;
    }
}
