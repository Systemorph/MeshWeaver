using MeshWeaver.Data;
using MeshWeaver.Messaging;
using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// LEGACY configuration for persisted TrackedChange satellite nodes.
/// <para>
/// 🚨 Nothing writes these any more — tracked changes are a view model projected from the version
/// history (<see cref="ChangeProjection"/>). This registration survives for the deprecation window
/// so <c>_Tracking</c> rows written by older builds stay readable and permission-delegating (the
/// central Collaboration plugin keeps a legacy accept/reject reader). It goes away together with
/// <see cref="AnnotationExtensions.TrackingPartition"/> and the <c>_Tracking → annotations</c> table
/// mapping once no deployment carries such rows.
/// </para>
/// TrackedChange nodes are satellite entities — excluded from search and create contexts.
/// Access is delegated to the MainNode (parent) via SatelliteAccessRule.
/// </summary>
public static class TrackedChangeNodeType
{
    /// <summary>
    /// The NodeType value used to identify tracked change nodes.
    /// </summary>
    public const string NodeType = "TrackedChange";

    /// <summary>
    /// Registers the built-in "TrackedChange" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddTrackedChangeType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new SatelliteAccessRule(NodeType, sp.GetRequiredService<IMessageHub>()));
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the TrackedChange node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "TrackedChange",
        Icon = "/static/NodeTypeIcons/document.svg",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<TrackedChange>())
    };
}
