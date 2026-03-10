using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for TrackedChange nodes in the graph.
/// TrackedChange nodes are satellite entities — excluded from search and create contexts.
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
        AssemblyLocation = typeof(TrackedChangeNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<TrackedChange>())
    };
}
