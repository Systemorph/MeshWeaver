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
        if (State is { Id: null })
            State = null;
    }

    public async Task<StreamInfo> Register(object address)
    {

        if (State == null)
            await InitializeState(address);

        return State;
    }

    public async Task Register(StreamInfo streamInfo)
    {
        if (Equals(State, streamInfo))
            return;
        logger.LogInformation("Registering {Stream} for address {Id}", streamInfo, this.GetPrimaryKeyString());
        State = streamInfo;
        await WriteStateAsync();
    }

    private async Task InitializeState(object address)
    {
        if (Node == null)
        {
            var id = this.GetPrimaryKeyString();
            Node = await meshCatalog.GetNodeAsync(id);
            if(Node == null)
                logger.LogInformation("No mesh node found for {Id}", id);
            else logger.LogInformation("Mapping {Id} to {Node}", id, Node);
        }
        State = Node != null
            ? new(this.GetPrimaryKeyString(), Node.StreamProvider, Node.Namespace, address)
            : new StreamInfo(SerializationExtensions.GetId(address), StreamProviders.Memory, IRoutingService.MessageIn, address); 
        
        logger.LogInformation("Mapping address {Address} for {Id} to {State}", address, this.GetPrimaryKeyString(),  State);
       await WriteStateAsync();
    }


    public Task<NodeStorageInfo> GetStorageInfo() =>
        Task.FromResult(Node == null ? null : new NodeStorageInfo(Node.Id, Node.BasePath, Node.AssemblyLocation, State.Address));

    public async Task Unregister()
    {
        await ClearStateAsync();
        DeactivateOnIdle();
    }
}


