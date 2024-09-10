using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans.Client
{
    public class OrleansRoutingService(IGrainFactory grainFactory, IMessageHub hub, ILogger<OrleansRoutingService> logger) : IRoutingService
    {
        private readonly IRoutingGrain routingGrain = grainFactory.GetGrain<IRoutingGrain>(hub.Address.ToString());

        public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
        {
            var ret = await routingGrain.DeliverMessage(delivery);
            if (ret.State == MessageDeliveryState.Submitted)
                return ret.Ignored();
            return ret;
        }


        public async Task RegisterHubAsync(IMessageHub hub)
        {
            var address = hub.Address;
            var addressId = address.ToString();
            var streamInfo = new StreamInfo(addressId, StreamProviders.Memory, hub.Address.GetType().Name, address);
            var info = await hub.ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<IAddressRegistryGrain>(streamInfo.Id).Register(address);
            
            var streamProvider = hub.ServiceProvider
                .GetKeyedService<IStreamProvider>(info.StreamProvider);

            logger.LogInformation("No stream provider found for {AddressId}", addressId);
            if (streamProvider == null)
                return;


            logger.LogInformation("Subscribing to {StreamProvider} {Namespace} {TargetId}", info.StreamProvider, info.Namespace, info.Id);
            var subscription = await streamProvider
                .GetStream<IMessageDelivery>(info.Namespace, info.Id)
                .SubscribeAsync((delivery, _) =>
                {
                    logger.LogDebug("Received {Delivery} for {Id}", delivery, info.Id);
                    return Task.FromResult(hub.DeliverMessage(delivery));
                });
            hub.WithDisposeAction(_ =>
            {
                logger.LogInformation("Unsubscribing from {StreamProvider} {Namespace} {TargetId}", info.StreamProvider, info.Namespace, info.Id);
                return subscription.UnsubscribeAsync();
            });
        }

    }
}
