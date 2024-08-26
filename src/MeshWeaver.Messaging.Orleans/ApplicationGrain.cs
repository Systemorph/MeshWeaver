using MeshWeaver.Messaging;
using MeshWeaver.Orleans.Contract;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Placement;
using Orleans.Providers;
using Orleans.Streams;

namespace MeshWeaver.Orleans;

public interface IRoutingGrain : IGrainWithStringKey
{
    Task<IMessageDelivery> DeliverMessage(IMessageDelivery request);
}
public class RoutingService(IGrainFactory grainFactory, IMessageHub hub) : IRoutingService
{
    private readonly IRoutingGrain routingGrain = grainFactory.GetGrain<IRoutingGrain>(hub.Address.ToString());

    public Task<IMessageDelivery> DeliverMessage(IMessageDelivery request)
        => routingGrain.DeliverMessage(request);
}


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



[PreferLocalPlacement]
[StorageProvider(ProviderName = StorageProviders.OrleansRedis)]
public class AddressRegistryGrain(ILogger<AddressRegistryGrain> logger, IMeshCatalog meshCatalog) : Grain<StreamInfo>, IAddressRegistryGrain
{
    private MeshNode Node { get; set; }

    public async Task<StreamInfo> Register(object address)
    {
        if (State != null)
            return State;

        if (Node == null)
        {
            Node = await meshCatalog.GetNodeAsync(address);
            logger.LogDebug("Mapping address {Address} to Id {Id} for {Node}", address, this.GetPrimaryKeyString(), Node);
        }
        State = ConvertNode(address);
        if(State != null)
            await WriteStateAsync();
        return State;
    }

    public async Task Register(StreamInfo streamInfo)
    {
        State = streamInfo;
        await WriteStateAsync();
    }

    private StreamInfo ConvertNode(object address) =>
        Node != null
            ? new(this.GetPrimaryKeyString(), Node.StreamProvider, Node.Namespace, address)
            :
            // TODO V10: What to do here? ==> we don't find route. Throw exception? (25.08.2024, Roland Bürgi)
            null;


    public Task<NodeStorageInfo> GetStorageInfo() =>
        Task.FromResult(Node == null ? null : new NodeStorageInfo(Node.Id, Node.BasePath, Node.AssemblyLocation, State.Address));

    public async Task Unregister()
    {
        await ClearStateAsync();
        DeactivateOnIdle();
    }
}



/// <summary>
/// Key is the full type name of the address type. 
/// </summary>
public interface IStreamingService
{
    public StreamInfo Get(object address);
}



