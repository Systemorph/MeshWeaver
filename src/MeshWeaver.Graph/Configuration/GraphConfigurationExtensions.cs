using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

    /// <param name="builder">The mesh builder</param>
    extension<TBuilder>(TBuilder builder) where TBuilder : MeshBuilder
    {
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
        /// <param name="dataDirectory">The base data directory</param>
        /// <param name="configuration">The application configuration</param>
        public TBuilder AddJsonGraphConfiguration(string dataDirectory,
            IConfiguration configuration)
        {
            // Register services that don't need hub-level dependencies at the mesh level
            builder.ConfigureServices(services =>
            {
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

            // Configure mesh hub with views and hub-level services
            // Note: Node types are compiled on-demand via IMeshNodeCompilationService.
            // MeshCatalog loads NodeTypeConfiguration from compiled assemblies when nodes are accessed.
            builder.ConfigureHub(config => config
                .AddMeshNodeView()
                .AddDynamicViews()
                .WithServices(services =>
                {
                    // These services need ITypeRegistry which is only available at hub level
                    services.AddSingleton<ITypeCompilationService, TypeCompilationService>();
                    services.AddSingleton<IMeshNodeCompilationService, MeshNodeCompilationService>();
                    return services;
                }));

            return builder;
        }

        /// <summary>
        /// Loads graph configuration from JSON files using the default data directory from configuration.
        /// Reads the data directory from Graph:DataDirectory section.
        /// </summary>
        public TBuilder AddJsonGraphConfiguration(IConfiguration configuration)
        {
            var graphSection = configuration.GetSection("Graph");
            var dataDirectoryConfig = graphSection["DataDirectory"] ?? "Data";
            var dataDirectory = Path.IsPathRooted(dataDirectoryConfig)
                ? dataDirectoryConfig
                : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), dataDirectoryConfig));

            return builder.AddJsonGraphConfiguration(dataDirectory, configuration);
        }
    }
}
