using MeshWeaver.Articles;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using Orleans.Providers;

namespace MeshWeaver.Hosting.Orleans;

[StorageProvider(ProviderName = StorageProviders.MeshCatalog)]
public class ArticleGrain(ILogger<ArticleGrain> logger) : Grain<Article>, IArticleGrain
{
    public Task<Article> Get(ArticleOptions options)
    {
        logger.LogDebug("Retrieving Article Entry {Article}", this.GetPrimaryKeyString());
        return Task.FromResult(State);
    }

    public async Task Update(Article entry)
    {
        logger.LogDebug("Updating Article to {entry}", entry);
        State = entry;
        await WriteStateAsync();
    }
}
