using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using MeshWeaver.Orleans.Client;
using Microsoft.Extensions.Logging;
using Orleans.Placement;
using Orleans.Providers;

namespace MeshWeaver.Orleans.Server;

[PreferLocalPlacement]
[StorageProvider(ProviderName = StorageProviders.OrleansRedis)]
public class AddressRegistryGrain(ILogger<AddressRegistryGrain> logger, IMeshCatalog meshCatalog) : Grain<StreamInfo>, IAddressRegistryGrain
{
    private MeshNode Node { get; set; }

    public async Task<StreamInfo> Register(object address)
    {
        if (State is { StreamProvider: not null })
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
            new StreamInfo(SerializationExtensions.GetId(address), StreamProviders.Memory, MeshNode.MessageIn, address);


    public Task<NodeStorageInfo> GetStorageInfo() =>
        Task.FromResult(Node == null ? null : new NodeStorageInfo(Node.Id, Node.BasePath, Node.AssemblyLocation, State.Address));

    public async Task Unregister()
    {
        await ClearStateAsync();
        DeactivateOnIdle();
    }
}


