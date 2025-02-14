using MeshWeaver.Connection.Orleans;
using MeshWeaver.Disposables;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans
{
    public class OrleansRoutingService(IGrainFactory grainFactory, IMessageHub hub, ILogger<OrleansRoutingService> logger) : RoutingServiceBase(hub)
    {

        public override Task UnregisterStreamAsync(Address address)
        {
            return Task.CompletedTask; // TODO V10: Provide proper implementation (13.02.2025, Roland Bürgi)
        }



        public override async Task<IAsyncDisposable> RegisterStreamAsync(Address address, AsyncDelivery callback)
        {
            TypeRegistry.WithType(address.GetType(), address.Type);
            var info = new MeshNode(address.Type, address.Id, address.ToString())
            {
                StreamProvider = StreamProviders.Mesh,
                Namespace = address
            };

            // TODO V10: Update storage if needed. (13.02.2025, Roland Bürgi)

            var streamProvider = Mesh.ServiceProvider
                .GetKeyedService<IStreamProvider>(info.StreamProvider);
            logger.LogInformation("Subscribing to {StreamProvider} {Namespace} {TargetId}", info.StreamProvider, info.Namespace, info.Name);

            var subscription = await streamProvider
                .GetStream<IMessageDelivery>(info.Namespace, info.Name)
                .SubscribeAsync((d, _) =>
                {
                    logger.LogDebug("Received {Delivery} for {Id}", d, info.Name);
                    return callback(d, default);
                });



            return new AnonymousAsyncDisposable(async () =>
            {
                await UnregisterStreamAsync(address);
                await subscription.UnsubscribeAsync();
            });

        }


        protected override async Task<IMessageDelivery> RouteImplAsync(IMessageDelivery delivery, 
            MeshNode node,
            Address address,
            CancellationToken cancellationToken)
        {
            if(address is HostedAddress hosted && Mesh.Address.Equals(hosted.Host))
                address = hosted.Address;

            // TODO V10: Consider caching locally. (09.02.2025, Roland Bürgi)
            var meshNode =
                await MeshCatalog.GetNodeAsync(address);
            if (meshNode is null)
                return delivery.Failed($"No mesh node found for {address.ToString()}");
            if (string.IsNullOrWhiteSpace(meshNode.StreamProvider))
            {
                await grainFactory.GetGrain<IMessageHubGrain>(address.ToString()).DeliverMessage(delivery);
                return delivery.Forwarded();
            }
            var streamProvider = Mesh.ServiceProvider
                .GetKeyedService<IStreamProvider>(meshNode.StreamProvider);

            logger.LogInformation("No stream provider found for {address}", address);
            if (streamProvider == null)
                return delivery.Failed($"No stream provider found with key {meshNode.StreamProvider}");

            await streamProvider.GetStream<IMessageDelivery>(meshNode.Namespace).OnNextAsync(delivery);
            return delivery.Forwarded();
        }


    }
}
