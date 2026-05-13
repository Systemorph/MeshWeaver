using MeshWeaver.Data;
using MeshWeaver.Graph.Security;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Activity nodes in the graph.
/// Activity nodes are system-generated satellite nodes — excluded from search and create contexts.
/// Access is delegated to the MainNode (parent) via SatelliteAccessRule.
/// </summary>
public static class ActivityNodeType
{
    public const string NodeType = "Activity";

    public static TBuilder AddActivityType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new SatelliteAccessRule(NodeType, sp.GetService<ISecurityService>() ?? new NullSecurityService()));
            return services;
        });
        return builder;
    }

    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Activity",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(ActivityNodeType).Assembly.Location,
        // Activity hubs host the kernel directly: SubmitCodeRequest etc. land here,
        // run inside this hub's action block, and write progress to the same
        // ActivityLog node via DataChangeRequest.Update on the local workspace.
        // Replaces the legacy `kernel/*` standalone hub addressing — replies route
        // through the standard MeshNode path instead of three special routing rules.
        HubConfiguration = config => config
            .AddActivityViews()
            .AddMeshDataSource(source => source
                .WithContentType<ActivityLog>())
            .AddKernelSubHubHandlers()
            // Per ActivityControlPlane doctrine: every long-running operation
            // runs on an Activity hub. Compile activities accept the
            // RunCompileRequest and own the Roslyn invocation here, leaving the
            // mesh hub and the parent NodeType hub responsive.
            .WithHandler<RunCompileRequest>(NodeTypeCompileActivityHandler.Handle)
    };
}
