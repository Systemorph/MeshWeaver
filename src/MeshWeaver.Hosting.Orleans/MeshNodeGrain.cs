using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
using Orleans.Providers;

namespace MeshWeaver.Hosting.Orleans;



[StorageProvider(ProviderName = StorageProviders.MeshCatalog)]
public class MeshNodeGrain(ILogger<MeshNode> logger) : Grain<MeshNode>, IMeshNodeGrain
{
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);
        if (State is { AddressId: null })
            State = null;

    }

    public Task<MeshNode> Get()
    {
        logger.LogDebug("Retrieving Application {State} Entry {Id}", State, this.GetPrimaryKeyString());
        return Task.FromResult(State);
    }

    public async Task Update(MeshNode entry)
    {
        logger.LogInformation("Updating MeshNode of {Id} to {Node}", this.GetPrimaryKeyString(), entry);
        State = entry;
        await WriteStateAsync();
    }

    public async Task Delete()
    {
        await ClearStateAsync();
        State = null;
    }
}
