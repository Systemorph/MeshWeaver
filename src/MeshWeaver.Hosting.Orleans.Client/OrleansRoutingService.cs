using MeshWeaver.Disposables;
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

        public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery, CancellationToken cancellationToken)
        {
            var ret = await routingGrain.DeliverMessage(delivery);
            if (ret.State == MessageDeliveryState.Submitted)
                return ret.Ignored();
            return ret;
        }



        public async Task<IDisposable> RegisterRouteAsync(string addressType, string id, AsyncDelivery delivery)
        {
            var streamInfo = new StreamInfo(id, StreamProviders.Memory, addressType, addressType);
            var info = await hub.ServiceProvider
            .GetRequiredService<IGrainFactory>()
            .GetGrain<IAddressRegistryGrain>(streamInfo.Id)
            .GetStreamInfo(addressType, id);

            var streamProvider = hub.ServiceProvider
                .GetKeyedService<IStreamProvider>(info.StreamProvider);

            logger.LogInformation("No stream provider found for {Id} of Type {Type}", id, addressType);
            if (streamProvider == null)
                return null;


            logger.LogInformation("Subscribing to {StreamProvider} {Namespace} {TargetId}", info.StreamProvider, info.Namespace, info.Id);
            var subscription = await streamProvider
                .GetStream<IMessageDelivery>(info.Namespace, info.Id)
                .SubscribeAsync((d, _) =>
                {
                    logger.LogDebug("Received {Delivery} for {Id}", delivery, info.Id);
                    return delivery.Invoke(d, default);
                });

            return new AnonymousDisposable(() =>
            {
                subscription.UnsubscribeAsync();

            });
        }
    }
}
