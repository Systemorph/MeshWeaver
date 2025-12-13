using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Messaging;

[assembly: InternalsVisibleTo("MeshWeaver.Connection.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith")]

namespace MeshWeaver.Mesh;

/// <summary>
/// Configuration for the mesh including registered nodes and node type configurations.
/// </summary>
public class MeshConfiguration(
    IReadOnlyDictionary<string, MeshNode> meshNodes,
    IReadOnlyDictionary<string, NodeTypeConfiguration> nodeTypeConfigurations)
{
    /// <summary>
    /// Registered mesh nodes by their key/path.
    /// </summary>
    public IReadOnlyDictionary<string, MeshNode> Nodes { get; } = meshNodes;

    /// <summary>
    /// Node type configurations that map NodeType strings to HubConfiguration and DataType.
    /// Used to configure hubs for persisted nodes based on their NodeType.
    /// </summary>
    public IReadOnlyDictionary<string, NodeTypeConfiguration> NodeTypeConfigurations { get; } = nodeTypeConfigurations;

    /// <summary>
    /// Gets the node type configuration for a given node type.
    /// </summary>
    public NodeTypeConfiguration? GetNodeTypeConfiguration(string? nodeType)
        => nodeType != null && NodeTypeConfigurations.TryGetValue(nodeType, out var config) ? config : null;
}
