using System.Reactive.Linq;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Services.LanguageServer;
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
                .AddNotificationRuleType()
                .AddNotificationChannelType()
                .AddInvitationType()
                .AddEmailType()
                .AddEaCredentialType()
                .AddTeamsConversationType()
                .AddGraphSubscriptionType()
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

            // Register Graph content types in the type registry for polymorphic
            // JSON serialization — on the mesh hub AND on every per-node hub.
            // The per-node overlay is essential: a NodeType definition hub posts
            // RunCompileRequest to its compile-activity hub and must deserialise
            // the RunCompileResponse that routes back; both hubs are per-node
            // hubs, so without the ConfigureDefaultNodeHub overlay the compile
            // round-trip fails with "type 'RunCompileResponse' is not registered
            // in this hub's TypeRegistry" and the compile never settles.
            builder.ConfigureHub(config => config.WithGraphTypes());
            // Every per-node hub gets MeshDataSource + Overview/Thumbnail/Settings/Search
            // as a baseline. Compiled NodeType HubConfigurations layer on top
            // (AddMeshDataSource is idempotent via its marker; WithView overrides
            // the default Overview/Settings when the NodeType provides its own).
            // Without this, per-instance hubs that activate before their NodeType
            // is compiled (or whose NodeType never compiles — seeded test definitions,
            // framework types that don't ship a Configuration string) get no
            // GetDataRequest handler at all → GetDataRequest returns NotFound,
            // ReadNodeAsync returns null. Repro: FileSystem_Organizations,
            // LinkedInProfile_NodeType_CompilesAndRendersOverview.
            builder.ConfigureDefaultNodeHub(config => config
                .WithGraphTypes()
                .AddDefaultLayoutAreas());

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
                    // for callers that need the concrete CompileAndGetConfigurations entry point.
                    services.AddSingleton<MeshNodeCompilationService>();
                    services.AddSingleton<IMeshNodeCompilationService>(sp => sp.GetRequiredService<MeshNodeCompilationService>());
                    // Stage-1 LSP language services over a NodeType's live CSharpCompilation
                    // — hover, completion, diagnostics, speculative pre-flight checks. Consumed
                    // by the Lsp* MCP tools + Coder agent's Lsp plugin. SpeculativeCompilation
                    // needs the NuGet resolver to handle #r directives in proposed source.
                    services.AddSingleton<SpeculativeCompilation>();
                    services.AddSingleton<IMeshLanguageService, MeshNodeLanguageService>();
                    services.AddSingleton<INodeConfigurationResolver, NodeConfigurationResolver>();
                    services.AddSingleton<IMeshNodeHubFactory, MeshNodeHubFactory>();
                    // Synced-query / namespace-bounded creatable-types lookup.
                    services.AddSingleton<ICreatableTypesProvider, CreatableTypesProvider>();
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
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
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
