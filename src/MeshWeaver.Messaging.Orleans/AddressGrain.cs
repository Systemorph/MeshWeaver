using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using MeshWeaver.Orleans.Client;
using Microsoft.Extensions.Logging;
using Orleans.Providers;

namespace MeshWeaver.Orleans.Server;



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
        logger.LogDebug("Retrieving Application {State} Entry {Application}", State, this.GetPrimaryKeyString());
        return Task.FromResult(State);
    }

    public async Task Update(MeshNode entry)
    {
        logger.LogDebug("Updating Application to {Application}", entry);
        State = entry;
        await WriteStateAsync();
    }

}
