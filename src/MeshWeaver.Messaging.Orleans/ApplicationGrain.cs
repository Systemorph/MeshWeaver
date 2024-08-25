using MeshWeaver.Messaging;
using MeshWeaver.Orleans.Contract;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Providers;
using Orleans.Streams;

namespace MeshWeaver.Orleans;

public class RoutingGrain(ILogger<RoutingGrain> logger, IMessageHub hub) : Grain, IRoutingGrain
{
    public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery message)
    {
        logger.LogDebug("Delivering Message {Message} from {Sender} to {Target}", message.Message, message.Sender, message.Target);
        var target = message.Target;
        if(target == null)
            return message;
        var targetId = SerializationExtensions.GetId(target);
        var streamInfo = await GrainFactory.GetGrain<IAddressMapGrain>(targetId).Get(targetId);
        var stream = this.GetStreamProvider(streamInfo.StreamProvider).GetStream<IMessageDelivery>(streamInfo.Namespace, targetId);
        await stream.OnNextAsync(message);
        return message.Forwarded([target]);
    }

}

public interface IAddressMapGrain : IGrainWithStringKey
{
    Task<StreamInfo> Get(object address);
    Task<string> GetNodeId();

    Task<StartupInfo> GetStartupInfo();
}

public record StartupInfo(string NodeId, string BaseDirectory, string AssemblyLocation, object Address);

public class AddressMapGrain(ILogger<AddressMapGrain> logger, IMeshCatalog meshCatalog) : Grain, IAddressMapGrain
{
    private MeshNode Node { get; set; }
    private object Address { get; set; }

    public async Task<StreamInfo> Get(object address)
    {
        Address = address;
        Node ??= await meshCatalog.GetNodeAsync(address);
        return ConvertNode();
    }

    private StreamInfo ConvertNode() =>
        Node != null
            ? new(Node.Id, Node.StreamProvider, Node.Namespace)
            :
            // TODO V10: What to do here? ==> we don't find route. Throw exception? (25.08.2024, Roland Bürgi)
            null;

    public Task<string> GetNodeId()
        => Task.FromResult(Node?.Id);

    public Task<StartupInfo> GetStartupInfo() =>
        Task.FromResult(Node == null ? null : new StartupInfo(Node.Id, Node.BaseDirectory, Node.AssemblyLocation, Address));
}

public record StreamInfo(string CatalogId, string StreamProvider, string Namespace)
{
    public Guid StreamId { get; init; } = Guid.NewGuid();
}


/// <summary>
/// Key is the full type name of the address type. 
/// </summary>
public interface IStreamingService
{
    public StreamInfo Get(object address);
}

