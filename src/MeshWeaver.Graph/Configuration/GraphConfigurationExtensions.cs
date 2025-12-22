using MeshWeaver.Domain;
using MeshWeaver.Mesh;
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
    /// Configuration is loaded from _config/ subdirectories:
    /// - _config/dataModels/ - Data type definitions with inline C# source
    /// - _config/nodeTypes/ - Node type to data model mappings (including content collection mappings)
    /// - _config/hubFeatures/ - Hub feature configurations
    ///
    /// Content collections are configured per node type via contentCollections in nodeTypes/*.json.
    /// All configuration loading, type compilation, and service initialization
    /// happens asynchronously during hub initialization via WithInitialization.
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <param name="dataDirectory">The base data directory containing _config/ folder</param>
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

            return services;
        });
        var graphNode = new MeshNode("graph")
        {
            Name = "Graph",
            NodeType = "graph",
            Description = "Root of hierarchical graph",
            IconName = "Diagram",
            DisplayOrder = 1,
            HubConfiguration = hubConfig => ConfigureGraphHub(hubConfig, dataDirectory)
        };

        builder.AddMeshNodes([graphNode]);
        return builder;
    }

    /// <summary>
    /// Configures a graph hub with async initialization for loading configuration,
    /// compiling types, and initializing services.
    /// Content collections are configured per node type via NodeTypeConfig.ContentCollections.
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

            services.AddSingleton<IConfigurationStorageService>(sp =>
            {
                var registry = sp.GetRequiredService<ITypeRegistry>();
                return new ConfigurationStorageService(dataDirectory, registry);
            });

            // Register DataModel initializer for type compilation
            services.AddSingleton<IConfigurationInitializer>(sp =>
                new DataModelInitializer(sp.GetRequiredService<ITypeCompilationService>()));

            // Register NodeType initializer to register NodeTypeConfigurations
            services.AddSingleton<IConfigurationInitializer, NodeTypeRegistrationInitializer>();

            // Register the main initializer
            services.AddSingleton<GraphConfigurationInitializer>(sp =>
                new GraphConfigurationInitializer(
                    sp.GetRequiredService<IConfigurationStorageService>(),
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
