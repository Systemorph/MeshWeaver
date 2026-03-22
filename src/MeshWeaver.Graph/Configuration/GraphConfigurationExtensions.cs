using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

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
                .AddCodeType()
                .AddMarkdownType()
                .AddHtmlType()
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
                .AddKernel()
                .AddApiTokenType()
                .AddMeshDataSourceType()
                .AddPartitionType()
                .AddGlobalSettingsType();

            // Register query routing rules for partition + table resolution
            builder
                // Rule: path/namespace → partition (first path segment)
                .AddQueryRoutingRule(query =>
                {
                    var path = query.Path;
                    if (string.IsNullOrEmpty(path)) return null;
                    var slash = path.IndexOf('/');
                    var firstSeg = slash > 0 ? path[..slash] : path;
                    return string.IsNullOrEmpty(firstSeg) ? null : new QueryRoutingHints { Partition = firstSeg };
                })
                // Rule: nodeType → partition for Admin-only types
                .AddQueryRoutingRule(query =>
                {
                    var nt = query.ExtractNodeType();
                    return nt switch
                    {
                        "Partition" or "Role" or "GlobalSettings" => new QueryRoutingHints { Partition = "Admin" },
                        _ => null
                    };
                })
                // Rule: nodeType → satellite table resolution
                .AddQueryRoutingRule(query =>
                {
                    var nt = query.ExtractNodeType();
                    if (string.IsNullOrEmpty(nt)) return null;
                    var table = nt switch
                    {
                        "AccessAssignment" => "access",
                        "Thread" or "ThreadMessage" => "threads",
                        "Activity" or "ActivityLog" => "activities",
                        "UserActivity" => "user_activities",
                        "Comment" => "annotations",
                        "TrackedChange" or "Approval" => "annotations",
                        _ => null
                    };
                    return table != null ? new QueryRoutingHints { Table = table } : null;
                });

            // Register Graph content types in the hub's type registry for polymorphic JSON serialization
            builder.ConfigureHub(config => config.WithGraphTypes());

            // Register services that don't need hub-level dependencies at the mesh level
            builder.ConfigureServices(services =>
            {
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
                    services.AddSingleton<INodeConfigurationResolver, NodeConfigurationResolver>();
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

            // Get CodeConfiguration from child MeshNodes under the _Source path
            CodeConfiguration? codeFile = null;
            var codeParentPath = $"{nodeTypePath}/_Source";
            await foreach (var child in meshQuery.QueryAsync<MeshNode>($"namespace:{codeParentPath} scope:subtree").WithCancellation(ct))
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
