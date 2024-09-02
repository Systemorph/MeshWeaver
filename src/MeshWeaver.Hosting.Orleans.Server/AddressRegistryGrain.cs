using MeshWeaver.Hosting.Orleans.Client;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Orleans.Placement;
using Orleans.Providers;

namespace MeshWeaver.Hosting.Orleans.Server;

[PreferLocalPlacement]
[StorageProvider(ProviderName = StorageProviders.Redis)]
public class AddressRegistryGrain(ILogger<AddressRegistryGrain> logger, IMeshCatalog meshCatalog) : Grain<StreamInfo>, IAddressRegistryGrain
{
    private MeshNode Node { get; set; }
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);
        Node = await meshCatalog.GetNodeAsync(this.GetPrimaryKeyString());
        if (Node is { Id: null })
            Node = null;
        State = Node != null ? InitializeState(Node.Address) : null;
    }

    public async Task<StreamInfo> Register(object address)
    {

        if (State == null)
        {
            State = InitializeState(address);
            await WriteStateAsync();
        }

        logger.LogDebug("Mapping address {Address} to Id {Id} for {Node}", address, this.GetPrimaryKeyString(), Node);
        return State;
    }

    public async Task Register(StreamInfo streamInfo)
    {
        if (Equals(State, streamInfo))
            return;
        State = streamInfo;
        await WriteStateAsync();
    }

    private StreamInfo InitializeState(object address) =>
        Node != null 
            ? new(this.GetPrimaryKeyString(), Node.StreamProvider, Node.Namespace, address)
            :
            new StreamInfo(SerializationExtensions.GetId(address), StreamProviders.Memory, IRoutingService.MessageIn, address);


    public Task<NodeStorageInfo> GetStorageInfo() =>
        Task.FromResult(Node == null ? null : new NodeStorageInfo(Node.Id, Node.BasePath, Node.AssemblyLocation, State.Address));

    public async Task Unregister()
    {
        await ClearStateAsync();
        DeactivateOnIdle();
    }
}


