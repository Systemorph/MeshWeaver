using MeshWeaver.Hosting.Orleans.Client;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Orleans.Placement;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans.Server
{
    [PreferLocalPlacement]
    public class RoutingGrain(ILogger<RoutingGrain> logger) : Grain, IRoutingGrain
    {
        public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
        {
            logger.LogDebug("Delivering Message {Message} from {Sender} to {Target}", delivery.Message, delivery.Sender, delivery.Target);
            var target = delivery.Target;
            var targetId = SerializationExtensions.GetId(target);
            var streamInfo = await GrainFactory.GetGrain<IAddressRegistryGrain>(targetId).Register(target);
            if(streamInfo.StreamProvider is StreamProviders.Mesh)
                return await GrainFactory.GetGrain<IMessageHubGrain>(targetId).DeliverMessage(delivery);
            var stream = this.GetStreamProvider(streamInfo.StreamProvider).GetStream<IMessageDelivery>(streamInfo.Namespace, targetId);
            await stream.OnNextAsync(delivery);
            return delivery.Forwarded([target]);
        }

    }
}
