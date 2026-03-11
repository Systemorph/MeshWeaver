using MeshWeaver.AI;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Extension methods for configuring graph node types and services.
/// </summary>
public static class GraphConfigurationExtensions
{
    /// <param name="builder">The mesh builder</param>
    extension<TBuilder>(TBuilder builder) where TBuilder : MeshBuilder
    {
        /// <summary>
        /// Registers all built-in graph node types and configures graph services.
        /// </summary>
        public TBuilder AddGraph()
        {
            builder
                .AddNodeTypeType()
                .AddAgentType()
                .AddCodeType()
                .AddMarkdownType()
                .AddHtmlType()
                .AddThreadType(config => config
                    .AddThreadViews()
                    .AddMeshDataSource(source => source
                        .WithContentType<AI.Thread>()
                        .WithType<ThreadMessage>(ThreadMessageNodeType.NodeType)))
                .AddThreadMessageType()
                .AddCommentType()
                .AddTrackedChangeType()
                .AddAccessAssignmentType()
                .AddPartitionAccessPolicyType()
                .AddUserType()
                .AddVUserType()
                .AddGroupType()
                .AddRoleType()
                .AddGroupMembershipType()
                .AddApprovalType()
                .AddNotificationType()
                .AddActivityType()
                .AddUserActivityType()
                .AddKernelType()
                .AddPortalType()
                .AddApiTokenType()
                .AddMeshDataSourceType()
                .AddPartitionType()
                .AddGlobalSettingsType();

            // Register services that don't need hub-level dependencies at the mesh level
            builder.ConfigureServices(services =>
            {
                // Register Graph configuration types in the mesh-level ITypeRegistry
                // for polymorphic JSON deserialization by FileSystemStorageAdapter.
                // This must happen at mesh level so the types are available before any hub is created.
                // All content types are registered centrally via WithGraphTypes().
                var typeRegistry = services.BuildServiceProvider().GetService<ITypeRegistry>();
                typeRegistry?.WithGraphTypes();

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
            builder.ConfigureHub(config => config
                .AddDefaultLayoutAreas()
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
            var meshQuery = hub.ServiceProvider.GetRequiredService<IMeshService>();

            // The node type path is the hub address (e.g., "type/Person")
            var nodeTypePath = hub.Address.ToString();

            // Get CodeConfiguration from child MeshNodes under the Code path
            CodeConfiguration? codeFile = null;
            var codeParentPath = $"{nodeTypePath}/Code";
            await foreach (var child in meshQuery.QueryAsync<MeshNode>($"namespace:{codeParentPath}").WithCancellation(ct))
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
