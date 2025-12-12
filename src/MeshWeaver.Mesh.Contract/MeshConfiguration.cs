using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Messaging;

[assembly: InternalsVisibleTo("MeshWeaver.Connection.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith")]

namespace MeshWeaver.Mesh;

public class MeshConfiguration(IReadOnlyDictionary<string, MeshNode> meshNodes)
{
    public IReadOnlyDictionary<string, MeshNode> Nodes { get; } = meshNodes;
}
