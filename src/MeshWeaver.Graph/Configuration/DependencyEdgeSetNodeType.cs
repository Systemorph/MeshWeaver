using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Registers the <see cref="DependencyEdgeSetPaths.NodeType"/> node type: one compiled source's
/// forward dependency edges, read by Roslyn at the end of its compile and stored at
/// <c>{NodeTypePath}/_Dependency/Forward</c> (<c>Doc/Architecture/DependencyGraph</c>).
///
/// <para>The node sits under the NodeType it describes, in that type's partition, so whoever can
/// read the type can read what it depends on — and nothing else is granted. It keeps NO history:
/// each compile replaces the whole set, and the set names the build it was read from
/// (<see cref="DependencyEdgeSet.BuildMvid"/>), so a snapshot of an earlier set would describe
/// bytes nothing serves any more.</para>
/// </summary>
public static class DependencyEdgeSetNodeType
{
    /// <summary>Adds the node type to the mesh and keeps it out of autocomplete.</summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    /// <returns>The same builder.</returns>
    public static TBuilder AddDependencyEdgeSetType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(DependencyEdgeSetPaths.NodeType);
        return builder;
    }

    /// <summary>The node-type definition.</summary>
    /// <returns>The <see cref="DependencyEdgeSetPaths.NodeType"/> MeshNode.</returns>
    public static MeshNode CreateMeshNode() => new(DependencyEdgeSetPaths.NodeType)
    {
        Name = "Dependency Edges",
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        Content = new NodeTypeDefinition { KeepsHistory = false },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source.WithContentType<DependencyEdgeSet>())
    };
}
