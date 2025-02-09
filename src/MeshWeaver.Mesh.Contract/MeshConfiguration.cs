using System.Runtime.CompilerServices;

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


    internal List<Func<string, string, MeshNode>> MeshNodeFactories { get;  } = [];

    public MeshConfiguration AddMeshNodeFactory(Func<string, string, MeshNode> meshNodeFactory)
    {
        MeshNodeFactories.Add(meshNodeFactory);
        return this;
    }

    internal Dictionary<(string AddressType, string AddressId), MeshNode> Nodes { get; } = new();

    public MeshConfiguration AddMeshNodes(params IEnumerable<MeshNode> nodes)
    {
        foreach (var node in nodes)
        {
            Nodes[(node.AddressType, node.AddressId)] = node;
        }

        return this;
    }
    internal Dictionary<Type, object> Properties { get; } = new();
    public T Get<T>() => (T)(Properties.GetValueOrDefault(typeof(T)) ?? default(T));
    public MeshConfiguration Set<T>(T value)
    {
        Properties[typeof(T)] = value;
        return this; 
    }
}
