using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Registers built-in NodeTypes that are part of the MeshWeaver.Graph assembly.
/// </summary>
public static class BuiltInNodeTypes
{
    /// <summary>
    /// The NodeType value used for NodeType definition nodes.
    /// </summary>
    public const string NodeTypeId = "NodeType";

    private static bool _initialized;

    /// <summary>
    /// Ensures built-in types are registered. Safe to call multiple times.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_initialized) return;
        _initialized = true;

        RegisterNodeType();
    }

    private static void RegisterNodeType()
    {
        var definition = new NodeTypeDefinition
        {
            Id = NodeTypeId,
            Namespace = "Type",
            DisplayName = "Node Type",
            Description = "A node type definition",
            IconName = "Code",
            DisplayOrder = 0
        };

        var node = MeshNode.FromPath("Type/NodeType") with
        {
            Name = "Node Type",
            NodeType = NodeTypeId,
            Description = "A node type definition",
            IconName = "Code",
            DisplayOrder = 0,
            Content = definition,
            HubConfiguration = config => config
                .AddNodeTypeView()
                .AddData(data => data.AddSource(source => source.WithType<CodeFile>()))
        };

        NodeTypeRegistry.Register(node);
    }
}
