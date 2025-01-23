using MeshWeaver.Disposables;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace MeshWeaver.Connection.Orleans
{
    public class OrleansRoutingService(IGrainFactory grainFactory, IMessageHub hub, ILogger<OrleansRoutingService> logger) : RoutingServiceBase(hub)
    {
        private readonly IRoutingGrain routingGrain = 
            grainFactory.GetGrain<IRoutingGrain>(hub.Address.ToString());





        protected override async Task UnsubscribeAsync(Address address)
        {
            await GetAddressRegistryGrain(address)
                .Unregister();
        }

        private IAddressRegistryGrain GetAddressRegistryGrain(Address address)
            => GetAddressRegistryGrain(address.Type, address.Id);
        private IAddressRegistryGrain GetAddressRegistryGrain(string addressType, string id)
        {
            return Mesh.ServiceProvider
                .GetRequiredService<IGrainFactory>()
                .GetGrain<IAddressRegistryGrain>($"{addressType}/{id}");
        }


        public override async Task<IAsyncDisposable> RegisterStreamAsync(Address address, AsyncDelivery callback)
        {
            var info = new StreamInfo(address.Type, address.Id, StreamProviders.Memory, IRoutingService.MessageIn);
            await GetAddressRegistryGrain(address)
                .RegisterStream(info);

            var streamProvider = Mesh.ServiceProvider
                .GetKeyedService<IStreamProvider>(info.StreamProvider);
            logger.LogInformation("Subscribing to {StreamProvider} {Namespace} {TargetId}", info.StreamProvider, info.Namespace, info.Id);

            var subscription = await streamProvider
                .GetStream<IMessageDelivery>(info.Namespace, info.Id)
                .SubscribeAsync((d, _) =>
                {
                    logger.LogDebug("Received {Delivery} for {Id}", d, info.Id);
                    return callback(d, default);
                });



            return new AnonymousAsyncDisposable(async () =>
            {
                await UnsubscribeAsync(address);
                await subscription.UnsubscribeAsync();
            });

        }


        protected override async Task<IMessageDelivery> RouteImplAsync(IMessageDelivery delivery, MeshNode node,
            Address address,
            CancellationToken cancellationToken)
        {
            var info =
                await Mesh.ServiceProvider
                    .GetRequiredService<IGrainFactory>()
                    .GetGrain<IAddressRegistryGrain>($"{address}")
                    .GetStreamInfo();

            if (info is null)
                return await routingGrain.DeliverMessage(delivery); 
            var streamProvider = Mesh.ServiceProvider
                .GetKeyedService<IStreamProvider>(info.StreamProvider);

            logger.LogInformation("No stream provider found for {address}", address);
            if (streamProvider == null)
                return delivery.Failed($"No stream provider found with key {info.StreamProvider}");

            await streamProvider.GetStream<IMessageDelivery>(info.Namespace).OnNextAsync(delivery);
            return delivery.Forwarded();
        }


    }
}
