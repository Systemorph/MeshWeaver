using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using Orleans.Streams;

namespace OpenSmc.Application.Orleans;

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
}
