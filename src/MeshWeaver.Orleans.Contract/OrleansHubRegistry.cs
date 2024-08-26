using System.Collections.Immutable;
using MeshWeaver.Application;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Streams;

namespace MeshWeaver.Orleans.Contract;

public static  class OrleansHubRegistry
{
    public static MessageHubConfiguration AddOrleansMesh<TAddress>(this MessageHubConfiguration conf, TAddress address, Func<OrleansMeshContext, OrleansMeshContext> configuration = null)
        => conf
            .WithTypes(typeof(TAddress))
            .WithRoutes(routes =>
            {
                var id = address.ToString();
                var routeGrain = routes.Hub.ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<IRoutingGrain>(id);
                return routes.RouteAddress<object>((target, delivery, _) => routeGrain.DeliverMessage(target, delivery));
            })
            .WithBuildupAction(async (hub, cancellationToken) =>
            {
                await hub.ServiceProvider.GetRequiredService<IMeshCatalog>().InitializeAsync(cancellationToken);
                await hub.RegisterAddressForStreamingAsync(address.ToString());
            })
            .Set(configuration)
    ;

    private static async Task RegisterAddressForStreamingAsync(this IMessageHub hub, string addressId)
    {
        var address = hub.Address;
        var streamInfo = new StreamInfo(addressId, StreamProviders.SMS, hub.Address.GetType().Name, address);
        var info = await hub.ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<IAddressRegistryGrain>(streamInfo.Id).Register(address);
        var subscription = await hub.ServiceProvider
            .GetRequiredKeyedService<IStreamProvider>(info.StreamProvider)
            .GetStream<IMessageDelivery>(info.Namespace, info.Id)
            .SubscribeAsync((delivery, _) => Task.FromResult(hub.DeliverMessage(delivery)));
        hub.WithDisposeAction(_ => subscription.UnsubscribeAsync());
    }

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
