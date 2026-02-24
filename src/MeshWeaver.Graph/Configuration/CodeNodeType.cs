using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Code nodes in the graph.
/// Code nodes represent source code files attached to NodeType definitions.
/// </summary>
public static class CodeNodeType
{
    /// <summary>
    /// The NodeType value used to identify code nodes.
    /// </summary>
    public const string NodeType = "Code";

    /// <summary>
    /// Registers the built-in "Code" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddCodeType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Code node type.
    /// This provides HubConfiguration for nodes with nodeType="Code".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Code",
        Icon = "/static/NodeTypeIcons/code.svg",
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(CodeNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<CodeConfiguration>())
            .AddCodeViews()
    };
}
