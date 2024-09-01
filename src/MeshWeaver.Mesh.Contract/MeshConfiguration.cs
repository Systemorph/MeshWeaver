using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using MeshWeaver.Application;
using MeshWeaver.Messaging;
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Orleans.Client")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith")]

namespace MeshWeaver.Mesh.Contract;

public record MeshConfiguration
{
    internal ImmutableList<string> InstallAtStartup { get; init; } = ImmutableList<string>.Empty;

    public MeshConfiguration InstallAssemblies(params string[] assemblyLocations)
        => this with { InstallAtStartup = InstallAtStartup.AddRange(assemblyLocations) };

    internal ImmutableList<Func<object, string>> AddressToMeshNodeMappers { get; init; }
        = ImmutableList<Func<object, string>>.Empty
            .Add(o => o is ApplicationAddress ? SerializationExtensions.GetId(o) : SerializationExtensions.GetTypeName(o));

    public MeshConfiguration WithAddressToMeshNodeIdMapping(Func<object, string> addressToMeshNodeMap)
        => this with { AddressToMeshNodeMappers = AddressToMeshNodeMappers.Insert(0, addressToMeshNodeMap) };


}
