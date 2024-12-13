using System.Collections.Concurrent;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Monolith;

public class MonolithMeshCatalog(IMessageHub hub, MeshConfiguration configuration) : MeshCatalogBase(configuration)
{
    private readonly ILogger<MonolithMeshCatalog> logger = hub.ServiceProvider.GetRequiredService<ILogger<MonolithMeshCatalog>>();
    private readonly ConcurrentDictionary<(string AddressType, string Id), MeshNode> meshNodes = new();
    private readonly ConcurrentDictionary<(string AddressType, string Id), IReadOnlyDictionary<string, MeshArticle>> articles = new();
    protected override Task<MeshNode> LoadMeshNode(string addressType, string id) => 
        Task.FromResult(meshNodes.GetValueOrDefault((addressType, id)));

    public override Task UpdateAsync(MeshNode node)
    {
        meshNodes[(node.AddressType, node.AddressId)] = node;
        // TODO V10: Delegate indexing to IMeshIndexService running on its own hub. (06.09.2024, Roland Bürgi)
        return Task.CompletedTask;
    }

    protected override Task InitializeNodeAsync(MeshNode node)
    {
        meshNodes[(node.AddressType, node.AddressId)] = node;
        return Task.CompletedTask;
    }

    public override async Task<MeshArticle> GetArticleAsync(string addressType, string nodeId, string id, bool includeContent)
    {
        var key = (addressType, nodeId);
        if (articles.TryGetValue(key, out var inner))
            return await IncludeContent(inner.GetValueOrDefault(id), includeContent);
        var node = await GetNodeAsync(addressType, nodeId);
        var articlesByApplication = await InitializeArticlesForNodeAsync(node).ToDictionaryAsync(x => x.Name);
        articles[key] = articlesByApplication;
        return await IncludeContent(articlesByApplication.GetValueOrDefault(id), includeContent);
    }

}
