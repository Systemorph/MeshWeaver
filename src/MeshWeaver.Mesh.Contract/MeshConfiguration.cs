using System.Collections.Immutable;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MeshWeaver.Connection.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith")]

namespace MeshWeaver.Mesh;

public record MeshConfiguration
{
    internal ImmutableList<string> InstallAtStartup { get; init; } = ImmutableList<string>.Empty;

    public MeshConfiguration InstallAssemblies(params string[] assemblyLocations)
        => this with { InstallAtStartup = InstallAtStartup.AddRange(assemblyLocations) };


    internal ImmutableList<Func<string, string, MeshNode>> MeshNodeFactories { get; init; } = [];

    public MeshConfiguration AddMeshNodeFactory(Func<string, string, MeshNode> meshNodeFactory)
        => this with { MeshNodeFactories = MeshNodeFactories.Add(meshNodeFactory) };

    internal ImmutableDictionary<(string AddressType, string AddressId), MeshNode> Nodes { get; init; } = ImmutableDictionary<(string AddressType, string AddressId), MeshNode>.Empty;

    public MeshConfiguration AddMeshNodes(params IEnumerable<MeshNode> nodes)
        => this with
        {
            Nodes = Nodes.SetItems(nodes
                .Select(n =>
                    new KeyValuePair<(string AddressType, string AddressId), MeshNode>((n.AddressType, n.AddressId), n)
                )
            )
        };
}
