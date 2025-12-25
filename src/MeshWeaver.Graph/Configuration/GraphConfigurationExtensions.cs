using MeshWeaver.Data;
using MeshWeaver.Domain;
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
        /// - Each NodeType node has:
        ///   - NodeTypeDefinition content with HubConfiguration lambda
        ///   - Optional CodeConfiguration in partition folder (codeConfiguration.json)
        ///
        /// Content collections are configured per node type via NodeTypeDefinition.ContentCollections.
        /// All configuration loading and service initialization happens at mesh startup.
        /// </summary>
        /// <param name="dataDirectory">The base data directory</param>
        /// <param name="configuration">The application configuration</param>
        public TBuilder AddJsonGraphConfiguration(string dataDirectory,
            IConfiguration configuration)
        {
            // Register services that don't need hub-level dependencies at the mesh level
            builder.ConfigureServices(services =>
            {
                // Register Graph configuration types in the mesh-level ITypeRegistry
                // for polymorphic JSON deserialization by FileSystemStorageAdapter.
                // This must happen at mesh level so the types are available before any hub is created.
                var typeRegistry = services.BuildServiceProvider().GetService<ITypeRegistry>();
                if (typeRegistry != null)
                {
                    typeRegistry.WithType(typeof(NodeTypeDefinition), nameof(NodeTypeDefinition));
                    typeRegistry.WithType(typeof(CodeConfiguration), nameof(CodeConfiguration));
                    typeRegistry.WithType(typeof(NodeTypeData), nameof(NodeTypeData));
                }

                // Register INodeTypeService
                services.AddSingleton<INodeTypeService>(sp =>
                {
                    var persistence = sp.GetRequiredService<IPersistenceService>();
                    var hub = sp.GetRequiredService<IMessageHub>();
                    return new NodeTypeService(persistence, hub);
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
                    services.AddSingleton<IMeshNodeCompilationService, MeshNodeCompilationService>();
                    return services;
                })
                .WithHandler<GetDataRequest>(HandleNodeTypeRequest));

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

    /// <summary>
    /// Handles GetDataRequest for NodeTypeReference.
    /// Returns NodeTypeData combining NodeTypeDefinition and CodeConfiguration.
    /// The node type is encoded in the hub address.
    /// </summary>
    private static async Task<IMessageDelivery> HandleNodeTypeRequest(
        IMessageHub hub,
        IMessageDelivery<GetDataRequest> request,
        CancellationToken ct)
    {
        // Only handle NodeTypeReference, let other references pass through
        if (request.Message.Reference is not NodeTypeReference)
            return request;

        try
        {
            var persistence = hub.ServiceProvider.GetService<IPersistenceService>();
            if (persistence == null)
            {
                hub.Post(new GetDataResponse(null, 0) { Error = "IPersistenceService not available" },
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            // The node type path is the hub address (e.g., "type/Person")
            var nodeTypePath = hub.Address.ToString();

            // Get the MeshNode for this NodeType
            var meshNode = await persistence.GetNodeAsync(nodeTypePath);
            if (meshNode == null)
            {
                hub.Post(new GetDataResponse(null, 0) { Error = $"NodeType at '{nodeTypePath}' not found" },
                    o => o.ResponseFor(request));
                return request.Processed();
            }

            var definition = meshNode.Content as NodeTypeDefinition;

            // Get CodeConfiguration from the partition
            CodeConfiguration? codeConfig = null;
            await foreach (var obj in persistence.GetPartitionObjectsAsync(nodeTypePath, null))
            {
                if (obj is CodeConfiguration cc)
                {
                    codeConfig = cc;
                    break;
                }
            }

            var nodeTypeData = new NodeTypeData
            {
                Id = definition?.Id ?? meshNode.Name ?? nodeTypePath,
                Definition = definition,
                Code = codeConfig,
                Path = nodeTypePath
            };

            hub.Post(new GetDataResponse(nodeTypeData, hub.Version), o => o.ResponseFor(request));
            return request.Processed();
        }
        catch (Exception ex)
        {
            hub.Post(new GetDataResponse(null, 0) { Error = ex.Message },
                o => o.ResponseFor(request));
            return request.Processed();
        }
    }
}
