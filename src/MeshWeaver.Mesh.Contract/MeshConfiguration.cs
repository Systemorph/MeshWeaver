using System.Runtime.CompilerServices;
using MeshWeaver.Messaging;

[assembly: InternalsVisibleTo("MeshWeaver.Connection.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith")]

namespace MeshWeaver.Mesh;

/// <summary>
/// Configuration for the mesh including registered nodes.
/// Types are treated as mesh nodes with nodeType="NodeType".
/// </summary>
public class MeshConfiguration(
    IReadOnlyDictionary<string, MeshNode> meshNodes,
    Func<MessageHubConfiguration, MessageHubConfiguration>? defaultNodeHubConfiguration = null)
{
    /// <summary>
    /// Registered mesh nodes by their key/path.
    /// </summary>
    public IReadOnlyDictionary<string, MeshNode> Nodes { get; } = meshNodes;

    /// <summary>
    /// Default configuration applied to all node hubs.
    /// Use this to set up content collections, views, or other configuration
    /// that should be available on every node hub.
    /// </summary>
    public Func<MessageHubConfiguration, MessageHubConfiguration>? DefaultNodeHubConfiguration { get; } = defaultNodeHubConfiguration;
}
