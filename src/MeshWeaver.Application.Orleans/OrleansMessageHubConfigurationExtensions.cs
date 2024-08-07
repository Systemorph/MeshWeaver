using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Messaging;
using Orleans.Streams;

namespace MeshWeaver.Application.Orleans;

public static class OrleansMessageHubConfigurationExtensions
{
    public static MessageHubConfiguration WithForwardThroughOrleansStream<TAddress>(this MessageHubConfiguration config, string streamNamespace, Func<TAddress, string> streamIdFunc) =>
        config
            .WithRoutes(forward =>
                forward.RouteAddress<TAddress>((routedAddress, d, _) => SendToStreamAsync(forward.Hub, streamNamespace, streamIdFunc(routedAddress), d.Package(forward.Hub.JsonSerializerOptions)))
            );

    private static async Task<IMessageDelivery> SendToStreamAsync(IMessageHub hub, string streamNamespace, string streamId, IMessageDelivery delivery)
    {
        var streamProvider = hub.ServiceProvider.GetRequiredKeyedService<IStreamProvider>(ApplicationStreamProviders.AppStreamProvider);
        var stream = streamProvider.GetStream<IMessageDelivery>(streamNamespace, streamId);
        await stream.OnNextAsync(delivery);
        return delivery.Forwarded();
    }

    public static MessageHubConfiguration WithForwardToOrleansGrain<TAddress, TGrainInterface>(this MessageHubConfiguration config, Func<TAddress, string> grainIdFunc, Func<TGrainInterface, IMessageDelivery, Task<IMessageDelivery>> grainDeliveryFunc)
        where TGrainInterface : IGrainWithStringKey
        => config
            .WithRoutes(forward =>
                forward.RouteAddress<TAddress>((routedAddress, d, _) => SendToGrainAsync(forward.Hub, d, grainIdFunc(routedAddress), grainDeliveryFunc))
            );

    private static async Task<IMessageDelivery> SendToGrainAsync<TGrainInterface>(IMessageHub hub, IMessageDelivery delivery, string grainId, Func<TGrainInterface, IMessageDelivery, Task<IMessageDelivery>> grainDeliveryFunc)
        where TGrainInterface : IGrainWithStringKey
    {
        var clusterClient = hub.ServiceProvider.GetRequiredService<IClusterClient>();
        var grain = clusterClient.GetGrain<TGrainInterface>(grainId);

        return await grainDeliveryFunc(grain, delivery);
    }
}
