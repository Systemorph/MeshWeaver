using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Orleans.Placement;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans
{
    [PreferLocalPlacement]
    public class RoutingGrain(ILogger<RoutingGrain> logger) : Grain, IRoutingGrain
    {
        public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
        {
            logger.LogDebug("Delivering Message {Message} from {Sender} to {Target}", delivery.Message, delivery.Sender, delivery.Target);
            var target = delivery.Target;
            var (targetType,targetId) = SerializationExtensions.GetAddressTypeAndId(target);
            var streamInfo = await GrainFactory.GetGrain<IAddressRegistryGrain>($"{targetType}/{targetId}").GetStreamInfo();
            if (streamInfo.StreamProvider is StreamProviders.Mesh)
            {
                logger.LogDebug("Forwarding Message {Message} from {Sender} to {Target}", delivery.Message, delivery.Sender, delivery.Target);
                return await GrainFactory.GetGrain<IMessageHubGrain>(targetId).DeliverMessage(delivery);
            }
            logger.LogDebug("Forwarding Message {Message} from {Sender} to {Target} to {StreamProvider}: {Namespace} {Target}", delivery.Message, delivery.Sender, delivery.Target, streamInfo.StreamProvider, streamInfo.Namespace, targetId);
            var stream = this.GetStreamProvider(streamInfo.StreamProvider).GetStream<IMessageDelivery>(streamInfo.Namespace, targetId);
            await stream.OnNextAsync(delivery);
            return delivery.Forwarded([target]);
        }

    }
}
