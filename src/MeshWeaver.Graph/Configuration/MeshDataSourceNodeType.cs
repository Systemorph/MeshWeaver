using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for MeshDataSource node types in the graph.
/// MeshDataSource nodes represent external data repositories (FileSystem, Postgres, Cosmos, etc.)
/// that can be browsed, configured, and installed into the mesh.
/// </summary>
public static class MeshDataSourceNodeType
{
    /// <summary>
    /// The NodeType value used to identify data source nodes.
    /// </summary>
    public const string NodeType = "MeshDataSource";

    /// <summary>
    /// The namespace under which data source nodes are stored.
    /// </summary>
    public const string SourcesNamespace = "_sources";

    /// <summary>
    /// Registers the built-in "MeshDataSource" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddMeshDataSourceType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the MeshDataSource node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Data Source",
        Icon = "/static/NodeTypeIcons/database.svg",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(MeshDataSourceNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddMeshDataSourceViews()
            .AddMeshDataSource(source => source.WithContentType<MeshDataSourceConfiguration>())
    };
}
