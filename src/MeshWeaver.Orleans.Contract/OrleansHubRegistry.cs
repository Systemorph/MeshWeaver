using System.Collections.Immutable;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Streams;

namespace MeshWeaver.Orleans.Contract;

public static  class OrleansHubRegistry
{
    public static MessageHubConfiguration ConfigureOrleansMesh<TAddress>(this MessageHubConfiguration conf, TAddress address)
        => conf.ConfigureOrleansMesh(address, x => x);

    public static MessageHubConfiguration ConfigureOrleansMesh<TAddress>(this MessageHubConfiguration conf, TAddress address, Func<OrleansMeshConfiguration, OrleansMeshConfiguration> configuration)
        => conf
            .WithTypes(typeof(TAddress))
            .WithRoutes(routes =>
            {
                var id = address.ToString();
                var routeGrain = routes.Hub.ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<IRoutingGrain>(id);
                return routes.RouteAddress<object>((target, delivery, _) => routeGrain.DeliverMessage(target, delivery));
            })
            .WithBuildupAction(async (hub,_) =>
            {
                await hub.RegisterAddressForStreamingAsync(address.ToString());
            })
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
}

public record OrleansMeshConfiguration
{
    internal ImmutableList<string> InstallAtStartup { get; init; } = ImmutableList<string>.Empty;

    public OrleansMeshConfiguration InstallAssemblies(params string[] assemblyLocations)
        => this with { InstallAtStartup = InstallAtStartup.AddRange(assemblyLocations) };
}
