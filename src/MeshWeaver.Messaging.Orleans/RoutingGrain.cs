using MeshWeaver.Messaging;
using MeshWeaver.Orleans.Client;
using Microsoft.Extensions.Logging;
using Orleans.Placement;
using Orleans.Streams;

namespace MeshWeaver.Orleans.Server
{
    [PreferLocalPlacement]
    public class RoutingGrain(ILogger<RoutingGrain> logger) : Grain, IRoutingGrain
    {
        public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery message)
        {
            logger.LogDebug("Delivering Message {Message} from {Sender} to {Target}", message.Message, message.Sender, message.Target);
            var target = message.Target;
            var targetId = SerializationExtensions.GetId(target);
            var streamInfo = await GrainFactory.GetGrain<IAddressRegistryGrain>(targetId).Register(targetId);
            var stream = this.GetStreamProvider(streamInfo.StreamProvider).GetStream<IMessageDelivery>(streamInfo.Namespace, targetId);
            await stream.OnNextAsync(message);
            return message.Forwarded([target]);
        }

    }
}
