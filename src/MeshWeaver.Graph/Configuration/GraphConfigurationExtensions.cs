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
    /// Adds default views to a node type hub:
    /// - DefaultViews: Details (markdown property view), Edit (standard editor)
    /// - MeshNodeView: Thumbnail, Metadata, Settings, Comments
    /// Details is set as the default view for empty path requests.
    /// This should be called for every node type to ensure consistent view availability.
    /// </summary>
    public static MessageHubConfiguration WithDefaultNodeViews(this MessageHubConfiguration config)
        => config
            .AddDefaultViews()
            .AddMeshNodeView();

    /// <summary>
    /// Loads graph configuration from JSON files in the data directory.
    ///
    /// Configuration is loaded from NodeType MeshNodes stored under Type/:
    /// - Each NodeType node has a partition folder containing:
    ///   - dataModel.json - Data type definition with inline C# source
    ///   - layoutAreas/*.json - Layout area configurations
    ///   - hubFeatures.json - Hub feature configuration (optional)
    ///
    /// Content collections are configured per node type via NodeTypeDefinition.ContentCollections.
    /// All configuration loading, type compilation, and service initialization
    /// happens at mesh startup.
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
        // Register services that don't need hub-level dependencies at the mesh level
        builder.ConfigureServices(services =>
        {
            // Register layout area initializer
            services.AddSingleton<IConfigurationInitializer, LayoutAreaInitializer>();

            // Register INodeTypeService
            services.AddSingleton<INodeTypeService>(sp =>
            {
                var persistence = sp.GetRequiredService<IPersistenceService>();
                return new NodeTypeService(persistence);
            });

            // Register compilation cache options
            services.AddOptions<CompilationCacheOptions>();

            // Register compilation cache service
            services.AddSingleton<ICompilationCacheService, CompilationCacheService>();

            return services;
        });

        // Configure mesh hub with views, hub-level services, and initialization
        builder.ConfigureHub(config => config
            .AddMeshCatalogView()
            .AddDynamicViews()
            .WithServices(services =>
            {
                // These services need ITypeRegistry which is only available at hub level
                services.AddSingleton<ITypeCompilationService, TypeCompilationService>();
                services.AddSingleton<IMeshNodeCompilationService, MeshNodeCompilationService>();
                services.AddSingleton<IConfigurationInitializer, DataModelInitializer>();
                services.AddSingleton<IConfigurationInitializer, NodeTypeRegistrationInitializer>();
                services.AddSingleton<GraphConfigurationInitializer>();
                return services;
            })
            .WithInitialization(hub =>
            {
                // Run graph configuration initialization synchronously at mesh startup
                var initializer = hub.ServiceProvider.GetRequiredService<GraphConfigurationInitializer>();
                initializer.InitializeAsync(hub, CancellationToken.None).GetAwaiter().GetResult();
            }));

        return builder;
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
