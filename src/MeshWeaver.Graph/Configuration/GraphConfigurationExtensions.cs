using MeshWeaver.AI;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Extension methods for configuring graph from JSON files.
/// </summary>
public static class GraphConfigurationExtensions
{
    /// <summary>
    /// The NodeType value used to identify agent nodes.
    /// </summary>
    public const string AgentNodeType = "Agent";

    /// <summary>
    /// The NodeType value used to identify markdown documentation nodes.
    /// </summary>
    public const string MarkdownNodeType = Configuration.MarkdownNodeType.NodeType;

    /// <param name="builder">The mesh builder</param>
    extension<TBuilder>(TBuilder builder) where TBuilder : MeshBuilder
    {
        /// <summary>
        /// Loads graph configuration from JSON files in the data directory.
        ///
        /// Configuration is loaded from NodeType MeshNodes stored under Type/:
        /// - Each NodeType node has:
        ///   - NodeTypeDefinition content with Configuration lambda
        ///   - Optional CodeConfiguration in partition folder (codeFile.json)
        ///
        /// Content collections are configured per node type via NodeTypeDefinition.ContentCollections.
        /// All configuration loading and service initialization happens at mesh startup.
        ///
        /// Note: This method does NOT configure content collections. Callers should configure
        /// storage collections and default node hub mappings separately based on their needs.
        /// See MemexConfiguration.ConfigureMemexMesh for an example.
        /// </summary>
        public TBuilder AddJsonGraphConfiguration(string dataDirectory)
        {
            var assemblyLocation = typeof(GraphConfigurationExtensions).Assembly.Location;

            // Register the built-in "NodeType" MeshNode
            // This provides HubConfiguration for nodes with nodeType="NodeType" (type definition nodes).
            builder.AddMeshNodes(new MeshNode(MeshNode.NodeTypePath)
            {
                Name = "Node Type",
                Description = "Definition for a node type",
                Icon = "/static/NodeTypeIcons/code.svg",
                AssemblyLocation = assemblyLocation,
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source
                        .WithContentType<NodeTypeDefinition>())
                    .AddNodeTypeView()
            });

            // Register the built-in "Agent" MeshNode
            // This provides HubConfiguration for nodes with nodeType="Agent" (AI agent configurations).
            builder.AddMeshNodes(new MeshNode(AgentNodeType)
            {
                Name = "Agent",
                Description = "AI Agent configuration",
                Icon = "/static/NodeTypeIcons/bot.svg",
                AssemblyLocation = assemblyLocation,
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source
                        .WithContentType<AgentConfiguration>())
                    .AddAgentView()
            });

            // Register the built-in "Markdown" MeshNode
            // This provides HubConfiguration for nodes with nodeType="Markdown" (markdown documentation nodes).
            builder.AddMeshNodes(Configuration.MarkdownNodeType.CreateMeshNode() with { AssemblyLocation = assemblyLocation });

            // Register the built-in "Thread" MeshNode
            // This provides HubConfiguration for nodes with nodeType="Thread" (AI conversation threads).
            builder.AddMeshNodes(Configuration.ThreadNodeConfiguration.CreateMeshNode() with { AssemblyLocation = assemblyLocation });

            // Register the built-in "ThreadMessage" MeshNode
            // This provides HubConfiguration for nodes with nodeType="ThreadMessage" (individual messages in threads).
            builder.AddMeshNodes(Configuration.ThreadMessageNodeConfiguration.CreateMeshNode() with { AssemblyLocation = assemblyLocation });

            // Register the built-in "Comment" MeshNode
            // This provides HubConfiguration for nodes with nodeType="Comment" (comments on document nodes).
            builder.AddMeshNodes(Configuration.CommentNodeConfiguration.CreateMeshNode() with { AssemblyLocation = assemblyLocation });

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
                    typeRegistry.WithType(typeof(AgentConfiguration), nameof(AgentConfiguration));
                    typeRegistry.WithType(typeof(AgentDelegation), nameof(AgentDelegation));
                    typeRegistry.WithType(typeof(Comment), nameof(Comment));
                    typeRegistry.WithType(typeof(MarkdownContent), nameof(MarkdownContent));
                    typeRegistry.WithType(typeof(MeshWeaver.AI.Thread), nameof(MeshWeaver.AI.Thread));
                    typeRegistry.WithType(typeof(ThreadMessage), nameof(ThreadMessage));
                }

                // Register compilation cache options
                services.AddOptions<CompilationCacheOptions>();

                // Register compilation cache service
                services.AddSingleton<ICompilationCacheService, CompilationCacheService>();

                return services;
            });

            // Configure mesh hub with views and hub-level services
            // Note: MeshDataSource is added automatically via NodeTypeService.WrapWithMeshDataSource
            // Node types are compiled on-demand via IMeshNodeCompilationService.
            // MeshCatalog loads NodeTypeConfiguration from compiled assemblies when nodes are accessed.
            // Content collections should be configured by the caller (e.g., MemexConfiguration.ConfigureMemexMesh).
            builder.ConfigureHub(config => MeshNodeLayoutAreas.AddDefaultLayoutAreas(config)
                .AddContentCollections()
                .AddEmbeddedResourceContentCollection("NodeTypeIcons",
                    typeof(GraphConfigurationExtensions).Assembly, "Icons")
                .WithServices(services =>
                {
                    // Register MeshNodeCompilationService as both concrete and interface
                    // NodeTypeService needs the concrete type for internal CompileToReleaseAsync method
                    services.AddSingleton<MeshNodeCompilationService>();
                    services.AddSingleton<IMeshNodeCompilationService>(sp => sp.GetRequiredService<MeshNodeCompilationService>());
                    services.AddSingleton<INodeTypeService, NodeTypeService>();
                    return services;
                })
                .WithHandler<GetDataRequest>(HandleNodeTypeRequest));

            return builder;
        }

    }

    /// <summary>
    /// Handles GetDataRequest for NodeTypeReference.
    /// Returns CodeConfiguration from the node's partition.
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

            // Get CodeConfiguration from child MeshNodes under the Code path
            CodeConfiguration? codeFile = null;
            var codeParentPath = $"{nodeTypePath}/Code";
            await foreach (var child in persistence.GetChildrenAsync(codeParentPath).WithCancellation(ct))
            {
                if (child.Content is CodeConfiguration cf)
                {
                    codeFile = cf;
                    break;
                }
            }

            hub.Post(new GetDataResponse(codeFile, hub.Version), o => o.ResponseFor(request));
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
