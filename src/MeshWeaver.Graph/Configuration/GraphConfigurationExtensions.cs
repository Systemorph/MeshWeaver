using System.Reactive.Linq;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.NuGet;
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
                .AddReleaseType()
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

            // Seed the built-in NodeCopy + Mirror script templates as Code MeshNodes
            // at Templates/Import/{NodeCopy,Mirror}. ImportLayoutArea fires
            // ExecuteScriptRequest at these to drive copy / mirror operations.
            // Stateless static helper, no DI provider — see
            // Doc/Architecture/AsynchronousCalls.md → "Static handlers compose".
            builder.AddMeshNodes(GraphImportTemplates.GetStaticNodes());

            // Register services that don't need hub-level dependencies at the mesh level
            builder.ConfigureServices(services =>
            {
                // Register compilation cache options
                services.AddOptions<CompilationCacheOptions>();

                // Register compilation cache service
                services.AddSingleton<ICompilationCacheService, CompilationCacheService>();

                // NuGet package resolver for #r "nuget:..." directives
                services.AddNuGetResolver();

                // Make this assembly visible to kernel scripts so the
                // NodeCopy + Mirror templates can resolve types from
                // MeshWeaver.Graph (NodeCopyHelper, …) without depending
                // on AppDomain having eagerly loaded them.
                services.AddSingleton(new MeshWeaver.Kernel.Hub.KernelScriptAssembly(
                    typeof(GraphImportTemplates).Assembly));

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
                // High-level subtree-copy operation. Relays NodeCopyDispatchRequest
                // through the Templates/Import/NodeCopy script template via
                // ScriptDispatch.RelayToScript so callers see request/response
                // semantics while the work runs as an Activity (live progress,
                // cancellation via RequestedStatus). Same shape as
                // ExportDocumentRequest. The legacy CopyNodeRequest stays for
                // single-node / move-internal paths.
                .AddNodeCopyDispatchHandler()
                .WithServices(services =>
                {
                    // Register MeshNodeCompilationService as both concrete and interface
                    // NodeTypeService needs the concrete type for internal CompileToReleaseAsync method
                    services.AddSingleton<MeshNodeCompilationService>();
                    services.AddSingleton<IMeshNodeCompilationService>(sp => sp.GetRequiredService<MeshNodeCompilationService>());
                    services.AddSingleton<INodeTypeService, NodeTypeService>();
                    services.AddSingleton<INodeConfigurationResolver, NodeConfigurationResolver>();
                    services.AddSingleton<IMeshNodeHubFactory, MeshNodeHubFactory>();
                    // Replacement for INodeTypeService.GetCreatableTypesAsync — synced-query
                    // based, namespace-bounded (no global nodeType:NodeType scan).
                    services.AddSingleton<ICreatableTypesProvider, CreatableTypesProvider>();
                    // Dedicated hosted hub for NodeType stream subscriptions —
                    // mesh hub must never be the requesting workspace for
                    // cross-hub remote streams during routing/activation.
                    services.AddSingleton<NodeTypeServiceHub>();
                    // Shared per-NodeType MeshNode streams (Replay(1).RefCount + 1h
                    // sliding eviction). Consumers that previously called
                    // workspace.GetMeshNodeStream(nodeTypePath) directly route
                    // through this cache so subscribers share one upstream.
                    services.AddSingleton<NodeTypeStreamCache>();
                    // Replay-cached PartitionDefinition lookups, used by
                    // CodeNodeType.HandleExecuteScript to resolve
                    // PartitionDefinition.DefaultActivityParentPath without
                    // bridging an async catalog query into the sync handler.
                    services.AddSingleton<PartitionRegistry>();
                    return services;
                })
                .WithHandler<GetDataRequest>(HandleNodeTypeRequest));

            return builder;
        }

    }

    /// <summary>
    /// Handles GetDataRequest for NodeTypeReference.
    /// Returns CodeConfiguration from the node's partition.
    /// Sync handler — composes via <c>IObservable</c>; no <c>await</c>.
    /// </summary>
    private static IMessageDelivery HandleNodeTypeRequest(
        IMessageHub hub,
        IMessageDelivery<GetDataRequest> request)
    {
        // Only handle NodeTypeReference, let other references pass through
        if (request.Message.Reference is not NodeTypeReference)
            return request;

        var meshQuery = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var nodeTypePath = hub.Address.ToString();
        var codeParentPath = $"{nodeTypePath}/{CodeNodeType.SourceSubNamespace}";

        meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{codeParentPath} scope:subtree"))
            .Take(1).Timeout(TimeSpan.FromSeconds(30))
            .Select(change => change.Items
                .Select(n => n.Content)
                .OfType<CodeConfiguration>()
                .FirstOrDefault())
            .Subscribe(
                codeFile => hub.Post(
                    new GetDataResponse(codeFile, hub.Version),
                    o => o.ResponseFor(request)),
                ex => hub.Post(
                    new GetDataResponse(null, 0) { Error = ex.Message },
                    o => o.ResponseFor(request)));

        return request.Processed();
    }
}
