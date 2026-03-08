using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for ActivityLog nodes in the graph.
/// ActivityLog nodes are system-generated -- excluded from search and create contexts.
/// </summary>
public static class ActivityLogNodeType
{
    /// <summary>
    /// The NodeType value used to identify activity log nodes.
    /// </summary>
    public const string NodeType = "ActivityLog";

    /// <summary>
    /// Registers the built-in "ActivityLog" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddActivityLogType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the ActivityLog node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Activity Log",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(ActivityLogNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddActivityLogViews()
            .AddMeshDataSource(source => source
                .WithContentType<ActivityLog>())
    };
}
