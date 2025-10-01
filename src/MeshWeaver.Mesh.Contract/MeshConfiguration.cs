using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Messaging;

[assembly: InternalsVisibleTo("MeshWeaver.Connection.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith")]

namespace MeshWeaver.Mesh;

public class MeshConfiguration(IReadOnlyDictionary<string, MeshNode> meshNodes, IReadOnlyCollection<Func<Address, MeshNode?>> factories)
{

    public IReadOnlyCollection<Func<Address, MeshNode?>> MeshNodeFactories { get; } = factories;


    public IReadOnlyDictionary<string, MeshNode> Nodes { get; } = meshNodes;

}
