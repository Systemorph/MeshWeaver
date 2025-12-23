using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Extension methods for configuring graph from JSON files.
/// </summary>
public static class GraphConfigurationExtensions
{
    /// <summary>
    /// Loads graph configuration from JSON files in the data directory.
    ///
    /// Configuration is loaded from NodeType MeshNodes stored under type/:
    /// - Each NodeType node has a partition folder containing:
    ///   - dataModel.json - Data type definition with inline C# source
    ///   - layoutAreas/*.json - Layout area configurations
    ///   - hubFeatures.json - Hub feature configuration (optional)
    ///
    /// The graph root node is loaded from persistence (graph.json) and its
    /// type definition comes from type/graph.
    ///
    /// Content collections are configured per node type via NodeTypeDefinition.ContentCollections.
    /// All configuration loading, type compilation, and service initialization
    /// happens asynchronously during hub initialization via WithInitialization.
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <param name="dataDirectory">The base data directory</param>
    /// <param name="configuration">The application configuration</param>
    public static TBuilder AddJsonGraphConfiguration<TBuilder>(
        this TBuilder builder,
        string dataDirectory,
        IConfiguration configuration)
        where TBuilder : MeshBuilder
    {
        // Register services at the mesh level that don't depend on ITypeRegistry
        builder.ConfigureServices(services =>
        {
            // Register layout area initializer (doesn't need ITypeRegistry)
            services.AddSingleton<IConfigurationInitializer, LayoutAreaInitializer>();

            // Register INodeTypeService (uses IPersistenceService which is registered elsewhere)
            services.AddSingleton<INodeTypeService>(sp =>
            {
                var persistence = sp.GetRequiredService<IPersistenceService>();
                return new NodeTypeService(persistence);
            });

            return services;
        });

        // Register the graph node for bootstrapping.
        // The node's NodeType is "type/graph" which explicitly references the type definition at that path.
        // The type definition (DataModel, LayoutAreas, etc.) is loaded from type/graph during initialization.
        // Only the HubConfiguration is set here for bootstrap - it runs initialization that loads all types.
        var graphNode = new MeshNode("graph")
        {
            NodeType = "type/graph", // Explicit reference to type definition at type/graph
            HubConfiguration = hubConfig => ConfigureGraphHub(hubConfig, dataDirectory)
        };

        builder.AddMeshNodes([graphNode]);
        return builder;
    }

    /// <summary>
    /// Configures a graph hub with async initialization for loading configuration,
    /// compiling types, and initializing services.
    /// Content collections are configured per node type via NodeTypeDefinition.ContentCollections.
    /// </summary>
    private static MessageHubConfiguration ConfigureGraphHub(
        MessageHubConfiguration config,
        string dataDirectory)
    {
        // Register services at the hub level where ITypeRegistry is available
        config = config
            .AddMeshCatalogView()
            .AddDynamicViews() // Enable dynamic view compilation and rendering
            .WithServices(services =>
        {
            services.AddSingleton<ITypeCompilationService>(sp =>
            {
                var registry = sp.GetRequiredService<ITypeRegistry>();
                return new TypeCompilationService(
                    registry,
                    sp.GetService<ILogger<TypeCompilationService>>());
            });

            // Register DataModel initializer for type compilation
            services.AddSingleton<IConfigurationInitializer>(sp =>
                new DataModelInitializer(sp.GetRequiredService<ITypeCompilationService>()));

            // Register NodeType initializer to register NodeTypeConfigurations
            services.AddSingleton<IConfigurationInitializer, NodeTypeRegistrationInitializer>();

            // Register the main initializer
            services.AddSingleton<GraphConfigurationInitializer>(sp =>
                new GraphConfigurationInitializer(
                    sp.GetRequiredService<INodeTypeService>(),
                    sp.GetServices<IConfigurationInitializer>(),
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<GraphConfigurationInitializer>()));

            return services;
        });

        return config.WithInitialization(async (hub, ct) =>
        {
            var initializer = hub.ServiceProvider.GetRequiredService<GraphConfigurationInitializer>();
            await initializer.InitializeAsync(hub, ct);
        });
    }

    /// <summary>
    /// Loads graph configuration from JSON files using the default data directory from configuration.
    /// Reads the data directory from Graph:DataDirectory section.
    /// </summary>
    public static TBuilder AddJsonGraphConfiguration<TBuilder>(
        this TBuilder builder,
        IConfiguration configuration)
        where TBuilder : MeshBuilder
    {
        var graphSection = configuration.GetSection("Graph");
        var dataDirectoryConfig = graphSection["DataDirectory"] ?? "Data";
        var dataDirectory = Path.IsPathRooted(dataDirectoryConfig)
            ? dataDirectoryConfig
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), dataDirectoryConfig));

        return builder.AddJsonGraphConfiguration(dataDirectory, configuration);
    }
}
