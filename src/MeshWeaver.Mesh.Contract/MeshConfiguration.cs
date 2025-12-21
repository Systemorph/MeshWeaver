using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Messaging;

[assembly: InternalsVisibleTo("MeshWeaver.Connection.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith")]

namespace MeshWeaver.Mesh;

/// <summary>
/// Configuration for the mesh including registered nodes and node type configurations.
/// </summary>
public class MeshConfiguration
{
    private readonly Dictionary<string, NodeTypeConfiguration> _nodeTypeConfigurations;

    public MeshConfiguration(
        IReadOnlyDictionary<string, MeshNode> meshNodes,
        IReadOnlyDictionary<string, NodeTypeConfiguration> nodeTypeConfigurations)
    {
        Nodes = meshNodes;
        _nodeTypeConfigurations = new Dictionary<string, NodeTypeConfiguration>(nodeTypeConfigurations);
    }

    /// <summary>
    /// Registered mesh nodes by their key/path.
    /// </summary>
    public IReadOnlyDictionary<string, MeshNode> Nodes { get; }

    /// <summary>
    /// Node type configurations that map NodeType strings to HubConfiguration and DataType.
    /// Used to configure hubs for persisted nodes based on their NodeType.
    /// </summary>
    public IReadOnlyDictionary<string, NodeTypeConfiguration> NodeTypeConfigurations => _nodeTypeConfigurations;

    /// <summary>
    /// Gets the node type configuration for a given node type.
    /// </summary>
    public NodeTypeConfiguration? GetNodeTypeConfiguration(string? nodeType)
        => nodeType != null && _nodeTypeConfigurations.TryGetValue(nodeType, out var config) ? config : null;

    /// <summary>
    /// Registers a node type configuration dynamically (e.g., from JSON config).
    /// </summary>
    public void RegisterNodeTypeConfiguration(NodeTypeConfiguration config)
    {
        _nodeTypeConfigurations[config.NodeType] = config;
    }
}
