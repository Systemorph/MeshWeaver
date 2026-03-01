using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for PartitionAccessPolicy nodes in the graph.
/// PartitionAccessPolicy nodes cap the maximum effective permissions at a namespace scope.
/// </summary>
public static class PartitionAccessPolicyNodeType
{
    /// <summary>
    /// The NodeType value used to identify partition access policy nodes.
    /// </summary>
    public const string NodeType = "PartitionAccessPolicy";

    /// <summary>
    /// Registers the built-in "PartitionAccessPolicy" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddPartitionAccessPolicyType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the PartitionAccessPolicy node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Partition Access Policy",
        Icon = "/static/NodeTypeIcons/shield.svg",
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(PartitionAccessPolicyNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<PartitionAccessPolicy>())
    };
}
