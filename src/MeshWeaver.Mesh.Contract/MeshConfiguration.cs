using System.Runtime.CompilerServices;
using MeshWeaver.Messaging;

[assembly: InternalsVisibleTo("MeshWeaver.Connection.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith")]

namespace MeshWeaver.Mesh;

public class MeshConfiguration
{
    internal List<string> InstallAtStartup { get; } = new();

    public MeshConfiguration InstallAssemblies(params string[] assemblyLocations)
    {
        InstallAtStartup.AddRange(assemblyLocations);
        return this;
    }


    internal List<Func<Address, MeshNode>> MeshNodeFactories { get;  } = [];

    public MeshConfiguration AddMeshNodeFactory(Func<Address, MeshNode> meshNodeFactory)
    {
        MeshNodeFactories.Add(meshNodeFactory);
        return this;
    }

    internal Dictionary<string, MeshNode> Nodes { get; } = new();

    public MeshConfiguration AddMeshNodes(params IEnumerable<MeshNode> nodes)
    {
        foreach (var node in nodes)
        {
            Nodes[node.Key] = node;
        }

        return this;
    }
    internal Dictionary<Type, object?> Properties { get; } = new();
    public T? Get<T>() => (T?)(Properties.GetValueOrDefault(typeof(T)) ?? default(T));
    public MeshConfiguration Set<T>(T value)
    {
        Properties[typeof(T)] = value;
        return this; 
    }
}
