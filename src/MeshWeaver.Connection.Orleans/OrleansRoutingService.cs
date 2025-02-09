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

        protected override async Task UnsubscribeAsync(Address address)
        {
            await GetMeshNodeGrain(address)
                .Delete();
        }

        private IMeshNodeGrain GetMeshNodeGrain(Address address)
            => GetMeshNodeGrain(address.Type, address.Id);
        private IMeshNodeGrain GetMeshNodeGrain(string addressType, string id)
        {
            return Mesh.ServiceProvider
                .GetRequiredService<IGrainFactory>()
                .GetGrain<IMeshNodeGrain>($"{addressType}/{id}");
        }


        public override async Task<IAsyncDisposable> RegisterStreamAsync(Address address, AsyncDelivery callback)
        {
            TypeRegistry.WithType(address.GetType(), address.Type);
            var info = new MeshNode(address.Type, address.Id, address.ToString())
            {
                StreamProvider = StreamProviders.Mesh,
                Namespace = IRoutingService.MessageIn
            };
            await GetMeshNodeGrain(address)
                .Update(info);

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
                await UnsubscribeAsync(address);
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
                await MeshCatalog.GetNodeAsync(address.Type, address.Id);
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
