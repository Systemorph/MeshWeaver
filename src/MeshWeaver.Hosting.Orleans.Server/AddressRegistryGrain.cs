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

    public async Task<StreamInfo> GetStreamInfo(string addressType, string id)
    {

        if (State == null)
            await InitializeState(addressType, id);

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

    private async Task InitializeState(string addressType, string addressId)
    {
        var parts = this.GetPrimaryKeyString().Split('/');
        if (parts.Length == 2)
        {
            addressType = parts[0];
            addressId = parts[1];
        }
        else
        {
            throw new InvalidOperationException("Invalid primary key format. Expected format: addressType/id");
        }
        
        if (Node == null)
        {
            var id = this.GetPrimaryKeyString();
            Node = await meshCatalog.GetNodeAsync(id);
            if(Node == null)
                logger.LogInformation("No mesh node found for {Id}", id);
            else logger.LogInformation("Mapping {Id} to {Node}", id, Node);
        }
        State = Node != null
            ? new(addressId, Node.StreamProvider, Node.Namespace, addressType)
            : new StreamInfo(addressId, StreamProviders.Memory, IRoutingService.MessageIn, addressType); 
        
        logger.LogInformation("Mapping address {AddressId} of Type {AddressType} to {State}", addressId, addressType,  State);
       await WriteStateAsync();
    }


    public Task<NodeStorageInfo> GetStorageInfo() =>
        Task.FromResult(Node == null ? null : new NodeStorageInfo(Node.Id, Node.BasePath, Node.AssemblyLocation, State.AddressType));

    public async Task Unregister()
    {
        await ClearStateAsync();
        DeactivateOnIdle();
    }
}


