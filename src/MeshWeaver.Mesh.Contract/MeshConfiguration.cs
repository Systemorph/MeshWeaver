using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MeshWeaver.Connection.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith")]

namespace MeshWeaver.Mesh;

/// <summary>
/// Configuration for the mesh including registered nodes.
/// Types are treated as mesh nodes with nodeType="NodeType".
/// </summary>
public class MeshConfiguration(IReadOnlyDictionary<string, MeshNode> meshNodes)
{
    /// <summary>
    /// Registered mesh nodes by their key/path.
    /// </summary>
    public IReadOnlyDictionary<string, MeshNode> Nodes { get; } = meshNodes;
}
