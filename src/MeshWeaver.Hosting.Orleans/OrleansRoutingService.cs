using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans
{
    public class OrleansRoutingService(
        IGrainFactory grainFactory, 
        IMessageHub hub, 
        ILogger<OrleansRoutingService> logger,
        IServiceProvider serviceProvider
        ) : RoutingServiceBase(hub)
    {

        public override Task UnregisterStreamAsync(Address address)
        {
            return grainFactory.GetGrain<IStreamRegistryGrain>(address.ToString()).Unregister();
        }



        public override async Task<IAsyncDisposable> RegisterStreamAsync(Address address, AsyncDelivery callback)
        {
            TypeRegistry.WithType(address.GetType(), address.Type);
            await grainFactory.GetGrain<IStreamRegistryGrain>(address).Register(new(address.Type, address.Id, StreamProviders.Mesh, address.ToString()));
            var stream = serviceProvider.GetRequiredKeyedService<IStreamProvider>(StreamProviders.Mesh)
                .GetStream<IMessageDelivery>(address.ToString());
            var subscription = await stream.SubscribeAsync((v, e) =>
                callback.Invoke(v, CancellationToken.None));
            return new AnonymousAsyncDisposable(async () =>
            {
                await subscription.UnsubscribeAsync();
                await UnregisterStreamAsync(address);
            });

        }


        protected override async Task<IMessageDelivery> RouteImplAsync(IMessageDelivery delivery, 
            MeshNode node,
            Address address,
            CancellationToken cancellationToken)
        {
            if(address is HostedAddress hosted && Mesh.Address.Equals(hosted.Host))
                address = hosted.Address;

            var streamInfo = await grainFactory.GetGrain<IStreamRegistryGrain>(address.ToString()).Get();
            if (streamInfo is { StreamProvider: not null })
            {
                logger.LogDebug("Routing {Message} to {Provider} {Namespace}", delivery, streamInfo.StreamProvider, streamInfo.Namespace);
                return await SendToStream(delivery, address, streamInfo.StreamProvider, streamInfo.Namespace);

            }

            // TODO V10: Consider caching locally. (09.02.2025, Roland Bürgi)
            var meshNode =
                await MeshCatalog.GetNodeAsync(address);
            if (meshNode is null)
            {
                logger.LogWarning("No route found to {Target}", delivery.Target);
                return delivery.Failed($"Don't find any way to deliver messages to {delivery.Target}");
            }
            var providerName = meshNode.StreamProvider;
            if (string.IsNullOrWhiteSpace(providerName))
            {
                await grainFactory.GetGrain<IMessageHubGrain>(address.ToString()).DeliverMessage(delivery);
                return delivery.Forwarded();
            }
            var @namespace = meshNode.Namespace;
            return await SendToStream(delivery, address, providerName, @namespace);
        }

        private async Task<IMessageDelivery> SendToStream(IMessageDelivery delivery, Address address, string providerName, string @namespace)
        {
            var streamProvider = Mesh.ServiceProvider
                .GetKeyedService<IStreamProvider>(providerName);

            logger.LogWarning("No stream provider found for {address}", address);
            if (streamProvider == null)
                return delivery.Failed($"No stream provider found with key {providerName}");

            await streamProvider.GetStream<IMessageDelivery>(@namespace).OnNextAsync(delivery);
            return delivery.Forwarded();
        }
    }
}
