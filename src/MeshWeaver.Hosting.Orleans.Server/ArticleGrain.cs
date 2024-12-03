using MeshWeaver.Hosting.Orleans.Client;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
using Orleans.Providers;

namespace MeshWeaver.Hosting.Orleans.Server;

[StorageProvider(ProviderName = StorageProviders.MeshCatalog)]
public class ArticleGrain(ILogger<ArticleGrain> logger) : Grain<MeshArticle>, IArticleGrain
{
    public Task<MeshArticle> Get(bool includeContent)
    {
        logger.LogDebug("Retrieving Article Entry {Article}", this.GetPrimaryKeyString());
        return Task.FromResult(State);
    }

    public async Task Update(MeshArticle entry)
    {
        logger.LogDebug("Updating Article to {entry}", entry);
        State = entry;
        await WriteStateAsync();
    }
}
