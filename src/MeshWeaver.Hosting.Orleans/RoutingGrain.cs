using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Placement;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans
{
    [PreferLocalPlacement]
    public class RoutingGrain(ILogger<RoutingGrain> logger, IRoutingService routingService) : Grain, IRoutingGrain
    {
        public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
        {
            logger.LogInformation("Delivering message from {Sender} to {Target}", delivery.Sender, delivery.Target);
            await routingService.DeliverMessageAsync(delivery);
            return delivery.Forwarded();
        }

        public Task RegisterStream(Address address, string streamProvider, string streamNamespace)
        {
            logger.LogInformation("Delivering message for {Address} to {Provider} @ {Namespace}", address, streamProvider, streamNamespace);
            var streamProviderInstance = GetStreamProvider(streamProvider);
            var stream = streamProviderInstance.GetStream<IMessageDelivery>(streamNamespace);
            return routingService.RegisterStreamAsync(address, async (d,ct) =>
            {
                await stream.OnNextAsync(d);
                return d.Forwarded();
            });
        }

        public Task UnregisterStream(Address address)
        {
            return routingService.UnregisterStreamAsync(address);
        }

        private IStreamProvider GetStreamProvider(string streamProvider) => 
            ServiceProvider.GetRequiredKeyedService<IStreamProvider>(streamProvider);
    }
}
