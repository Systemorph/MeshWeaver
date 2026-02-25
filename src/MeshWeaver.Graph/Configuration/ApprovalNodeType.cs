using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Approval nodes in the graph.
/// Approval nodes are system-generated — excluded from search and create contexts.
/// </summary>
public static class ApprovalNodeType
{
    /// <summary>
    /// The NodeType value used to identify approval nodes.
    /// </summary>
    public const string NodeType = "Approval";

    /// <summary>
    /// Registers the built-in "Approval" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddApprovalType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Approval node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Approval",
        Icon = "/static/NodeTypeIcons/checkmark.svg",
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(ApprovalNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddApprovalViews()
            .AddMeshDataSource(source => source
                .WithContentType<Approval>())
    };
}
