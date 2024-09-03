using MeshWeaver.Hosting.Orleans.Client;
using MeshWeaver.Mesh.Contract;
using Microsoft.Extensions.Logging;
using Orleans.Providers;

namespace MeshWeaver.Hosting.Orleans.Server;



[StorageProvider(ProviderName = StorageProviders.MeshCatalog)]
public class MeshNodeGrain(ILogger<MeshNode> logger) : Grain<MeshNode>, IMeshNodeGrain
{
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);
        if (State is { Id: null })
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

}
