using System.Collections.Immutable;
using MeshWeaver.Application;
using MeshWeaver.Messaging;

namespace MeshWeaver.Orleans.Client
{
    public record OrleansMeshContext
    {
        internal ImmutableList<string> InstallAtStartup { get; init; } = ImmutableList<string>.Empty;

        public OrleansMeshContext InstallAssemblies(params string[] assemblyLocations)
            => this with { InstallAtStartup = InstallAtStartup.AddRange(assemblyLocations) };

        internal ImmutableList<Func<object, string>> AddressToMeshNodeMappers { get; init; }
            = ImmutableList<Func<object, string>>.Empty
                .Add(o => o is ApplicationAddress ? SerializationExtensions.GetId(o) : null)
                .Add(SerializationExtensions.GetTypeName);

        public OrleansMeshContext WithAddressToMeshNodeIdMapping(Func<object, string> addressToMeshNodeMap)
            => this with { AddressToMeshNodeMappers = AddressToMeshNodeMappers.Insert(0, addressToMeshNodeMap) };


    }
}
