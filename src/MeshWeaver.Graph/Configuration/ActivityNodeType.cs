using MeshWeaver.Data;
using MeshWeaver.Graph.Security;
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
        HubConfiguration = config => config
            .AddActivityViews()
            .AddMeshDataSource(source => source
                .WithContentType<ActivityLog>())
    };
}
