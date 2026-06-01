using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Domain;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

[assembly: InternalsVisibleTo("MeshWeaver.Hosting")]
namespace MeshWeaver.Mesh;

/// <summary>
/// Builder for configuring a mesh instance including hub configuration, services, and mesh nodes.
/// </summary>
public record MeshBuilder
{
    /// <summary>
    /// Initializes a new instance of the MeshBuilder.
    /// </summary>
    /// <param name="ServiceConfig">Action to configure services in the DI container.</param>
    /// <param name="Address">The address of the mesh hub.</param>
    public MeshBuilder(Action<Func<IServiceCollection, IServiceCollection>> ServiceConfig, Address Address)
    {
        this.ServiceConfig = ServiceConfig;
        this.Address = Address;
        Register();
    }

    private List<MeshNode> MeshNodes { get; } = new();

    /// <summary>
    /// Installs mesh nodes from the specified assembly locations.
    /// </summary>
    /// <param name="assemblyLocations">Paths to assemblies containing MeshNodeProviderAttribute definitions.</param>
    /// <returns>The builder for method chaining.</returns>
    public MeshBuilder InstallAssemblies(params string[] assemblyLocations)
    {
        var attributes = assemblyLocations
            .Select(Assembly.LoadFrom)
            .SelectMany(a => a.GetCustomAttributes<MeshNodeProviderAttribute>())
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

    /// <summary>
    /// Adds configuration to the mesh hub.
    /// </summary>
    /// <param name="hubConfiguration">Function to configure the message hub.</param>
    /// <returns>The builder for method chaining.</returns>
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

    /// <summary>
    /// Configures services in the dependency injection container.
    /// </summary>
    /// <param name="configuration">Function to configure services.</param>
    /// <returns>The builder for method chaining.</returns>
    public MeshBuilder ConfigureServices(Func<IServiceCollection, IServiceCollection> configuration)
    {
        ServiceConfig.Invoke(configuration);
        return this;
    }
    private Action<Func<IServiceCollection, IServiceCollection>> ServiceConfig { get; init; }

    /// <summary>
    /// Gets the address of the mesh hub.
    /// </summary>
    public Address Address { get; init; }

    /// <summary>
    /// Configures the mesh settings.
    /// </summary>
    /// <param name="configuration">Function to configure the mesh.</param>
    /// <returns>The builder for method chaining.</returns>
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

        // Capture the list references - will be populated by builder calls later
        // The lambdas are evaluated when services are resolved (after all builder calls)
        var defaultNodeHubConfigs = DefaultNodeHubConfiguration;
        var meshTypeRegs = MeshTypeRegistrations;
        var excludedTypes = AutocompleteExcludedTypes;
        var accessConfig = NodeTypeAccessConfig;
        var routingRules = QueryRoutingRules;
        var streamRoutedTypes = StreamRoutedAddressTypes;

        ConfigureServices(services => services
            .AddSingleton(_ =>
            {
                // Evaluate defaultNodeHubConfigs at service resolution time, not at Register() time
                Func<MessageHubConfiguration, MessageHubConfiguration>? combinedDefaultConfig =
                    defaultNodeHubConfigs.Count > 0
                        ? config => defaultNodeHubConfigs.Aggregate(config, (c, f) => f(c))
                        : null;
                return new MeshConfiguration(
                    // Internal-only list — MeshConfiguration uses it to compute
                    // derived lazies (ContextExcludedTypes / SatelliteNodeTypes);
                    // no public property exposes it. Application code reads
                    // static nodes via serviceProvider.EnumerateStaticNodes().
                    MeshNodes,
                    combinedDefaultConfig,
                    autocompleteExcludedNodeTypes: excludedTypes.Count > 0 ? excludedTypes : null,
                    nodeTypePermissions: accessConfig.Build(),
                    queryRoutingRules: routingRules,
                    streamRoutedAddressTypes: streamRoutedTypes);
            })
            // Static nodes registered via AddMeshNodes(...) flow as an
            // IStaticNodeProvider. Application code reads them via
            // serviceProvider.EnumerateStaticNodes() — there is no Nodes
            // dictionary on MeshConfiguration. Last-write-wins by Path is
            // applied at iteration time inside the provider.
            .AddSingleton<IStaticNodeProvider>(new StaticMeshNodeListProvider(MeshNodes))
            .AddSingleton<ITypeRegistry>(_ =>
            {
                // Register core mesh types on the shared registry so they're available to ALL hubs
                // This ensures proper $type serialization across hub boundaries
                meshTypeRegistry.WithType(typeof(MeshNode), nameof(MeshNode));
                meshTypeRegistry.WithType(typeof(MeshNodeState), nameof(MeshNodeState));
                meshTypeRegistry.WithType(typeof(PingRequest), nameof(PingRequest));
                meshTypeRegistry.WithType(typeof(PingResponse), nameof(PingResponse));
                meshTypeRegistry.WithType(typeof(CreateNodeRequest), nameof(CreateNodeRequest));
                meshTypeRegistry.WithType(typeof(CreateNodeResponse), nameof(CreateNodeResponse));
                meshTypeRegistry.WithType(typeof(DeleteNodeRequest), nameof(DeleteNodeRequest));
                meshTypeRegistry.WithType(typeof(DeleteNodeResponse), nameof(DeleteNodeResponse));
                meshTypeRegistry.WithType(typeof(UpdateNodeRequest), nameof(UpdateNodeRequest));
                meshTypeRegistry.WithType(typeof(UpdateNodeResponse), nameof(UpdateNodeResponse));
                meshTypeRegistry.WithType(typeof(ExecuteScriptRequest), nameof(ExecuteScriptRequest));
                meshTypeRegistry.WithType(typeof(ExecuteScriptResponse), nameof(ExecuteScriptResponse));

                // Register additional types added via WithMeshType()
                foreach (var (type, name) in meshTypeRegs)
                    meshTypeRegistry.WithType(type, name);

                return meshTypeRegistry;
            })
            .AddSingleton(BuildHub)
            .AddSingleton<AccessService>()
            // Controlled I/O pools — mesh-scoped governor over the shared
            // ThreadPool for genuinely-async / sync-blocking leaves (file system,
            // blob, …). Resolved by leaf adapters via IoPoolRegistry; dies with
            // the mesh. See Doc/Architecture/ControlledIoPooling.md.
            .AddIoPools()
            );

        IReadOnlyCollection<Func<MeshConfiguration, MeshConfiguration>> meshConfig = MeshConfiguration;

        ConfigureHub(conf => conf.WithRoutes(routes =>
                // Observable-shaped handler — no Task<T>, no .FirstAsync().ToTask()
                // at the call site. The framework bridges once at the rule-chain
                // edge inside RouteConfiguration.WithHandler. Per
                // Doc/Architecture/AsynchronousCalls.md.
                routes.WithHandler(delivery =>
                {
                    // Compare without Host since Host tracks routing path
                    var targetWithoutHost = delivery.Target is not null ? delivery.Target with { Host = null } : null;
                    if (delivery.State != MessageDeliveryState.Submitted || targetWithoutHost == null || targetWithoutHost.Equals(Address))
                        return Observable.Return(delivery);

                    return routes.Hub.ServiceProvider.GetRequiredService<IRoutingService>()
                        .DeliverMessage(delivery.Package());
                }))
            .Set(meshConfig)
        );
    }

    /// <summary>
    /// Builds the message hub from the configured settings.
    /// </summary>
    /// <param name="sp">The service provider to use for building the hub.</param>
    /// <returns>The configured message hub.</returns>
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

    /// <summary>
    /// Adds mesh nodes to the mesh configuration.
    /// </summary>
    /// <param name="nodes">The mesh nodes to add.</param>
    /// <returns>The builder for method chaining.</returns>
    public MeshBuilder AddMeshNodes(params IEnumerable<MeshNode> nodes)
    {
        MeshNodes.AddRange(nodes);
        return this;
    }

    /// <summary>
    /// Registers a type on the mesh-level TypeRegistry for cross-hub serialization.
    /// Use this to register content types that need to be serialized across hub boundaries.
    /// </summary>
    /// <typeparam name="T">The type to register.</typeparam>
    /// <param name="name">The short name for the type (defaults to type name).</param>
    /// <returns>The builder for method chaining.</returns>
    public MeshBuilder WithMeshType<T>(string? name = null)
    {
        MeshTypeRegistrations.Add((typeof(T), name ?? typeof(T).Name));
        return this;
    }

    /// <summary>
    /// Registers a type on the mesh-level TypeRegistry for cross-hub serialization.
    /// Use this to register content types that need to be serialized across hub boundaries.
    /// </summary>
    /// <param name="type">The type to register.</param>
    /// <param name="name">The short name for the type (defaults to type name).</param>
    /// <returns>The builder for method chaining.</returns>
    public MeshBuilder WithMeshType(Type type, string? name = null)
    {
        MeshTypeRegistrations.Add((type, name ?? type.Name));
        return this;
    }

    private List<(Type Type, string Name)> MeshTypeRegistrations { get; } = new();

    /// <summary>
    /// Adds node types to be excluded from autocomplete/search results.
    /// Use this for satellite types (Comment, Thread) and internal types (AccessAssignment).
    /// </summary>
    public MeshBuilder AddAutocompleteExcludedTypes(params string[] nodeTypes)
    {
        foreach (var t in nodeTypes)
            AutocompleteExcludedTypes.Add(t);
        return this;
    }

    private HashSet<string> AutocompleteExcludedTypes { get; } = new();

    /// <summary>
    /// Configures node type access permissions (e.g., public-read types).
    /// </summary>
    public MeshBuilder ConfigureNodeTypeAccess(Action<NodeTypeAccessBuilder> configure)
    {
        configure(NodeTypeAccessConfig);
        return this;
    }

    internal NodeTypeAccessBuilder NodeTypeAccessConfig { get; } = new();

    /// <summary>
    /// Registers a query routing rule that resolves partition and/or table hints from a ParsedQuery.
    /// Rules are applied in order during query execution; first non-null Partition/Table wins.
    /// Use this to restrict fan-out queries (e.g., nodeType:User → partition "User").
    /// </summary>
    public MeshBuilder AddQueryRoutingRule(QueryRoutingRule rule)
    {
        QueryRoutingRules.Add(rule);
        return this;
    }

    internal List<QueryRoutingRule> QueryRoutingRules { get; } = [];

    /// <summary>
    /// Declares an address-type prefix that routes via the cluster-wide
    /// Orleans memory stream rather than grain activation. Hubs at such
    /// addresses are expected to <see cref="IRoutingService.RegisterStream(IMessageHub)"/>
    /// in their <c>WithInitialization</c>. Built-in defaults
    /// (<c>portal</c>, <c>client</c>) come from
    /// <see cref="MeshConfiguration.DefaultStreamRoutedAddressTypes"/>;
    /// modules add their own (e.g. <c>cache</c> for the mesh-node-cache
    /// hub) here. See <c>Doc/Architecture/OrleansTestRoutingPattern.md</c>.
    /// </summary>
    public MeshBuilder AddStreamRoutedAddressType(string addressType)
    {
        StreamRoutedAddressTypes.Add(addressType);
        return this;
    }

    internal HashSet<string> StreamRoutedAddressTypes { get; } =
        new(global::MeshWeaver.Mesh.MeshConfiguration.DefaultStreamRoutedAddressTypes, StringComparer.Ordinal);
}
