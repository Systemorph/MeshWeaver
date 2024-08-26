using System.Collections.Immutable;
using MeshWeaver.Application;
using MeshWeaver.Messaging;
using MeshWeaver.Orleans.Contract;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Orleans;

public static class OrleansExtensions
{
    public const string Storage = "storage";
    public static MessageHubConfiguration AddOrleansMesh<TAddress>(this MessageHubConfiguration conf, TAddress address, Func<OrleansMeshContext, OrleansMeshContext> configuration = null)
    => conf.AddOrleansMeshClient(address)
        .WithServices(services => 
            services.AddSingleton<IRoutingService, RoutingService>())
        .Set(configuration);

    private static Func<OrleansMeshContext, OrleansMeshContext> GetLambda(
        this MessageHubConfiguration config
    )
    {
        return config.Get<Func<OrleansMeshContext, OrleansMeshContext>>()
               ?? (x => x);
    }

    internal static OrleansMeshContext GetMeshContext(this MessageHubConfiguration config)
    {
        var dataPluginConfig = config.GetLambda();
        return dataPluginConfig.Invoke(new());
    }

}

public record OrleansAddress
{
    public string Id { get; set; } = Guid.NewGuid().AsString();

    public override string ToString()
        => $"o_{Id}";
}
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

