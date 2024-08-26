using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using MeshWeaver.Orleans.Client;
using Microsoft.Extensions.Logging;
using Orleans.Providers;

namespace MeshWeaver.Orleans.Server;



[StorageProvider(ProviderName = StorageProviders.MeshCatalog)]
public class MeshCatalogGrain(ILogger<MeshNode> logger) : Grain<MeshNode>, IMeshCatalogGrain
{
    public Task<MeshNode> GetEntry()
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
