using System.Collections.Concurrent;
using System.Reflection;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Monolith;

public class MonolithMeshCatalog(IMessageHub hub) : IMeshCatalog
{
    private readonly ConcurrentDictionary<string, MeshNode> meshNodes = new();
    private readonly ConcurrentDictionary<string, ArticleEntry> articles = new();
    private MeshConfiguration Configuration { get; } = hub.Configuration.GetMeshContext();


    public Task<MeshNode> GetNodeAsync(string id)
        => Task.FromResult(meshNodes.GetValueOrDefault(id));
    public Task UpdateAsync(MeshNode node)
    {
        meshNodes[node.Id] = node;
        return Task.CompletedTask;
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (var assemblyLocation in Configuration.InstallAtStartup)
        {
            var assembly = Assembly.LoadFrom(assemblyLocation);
            foreach (var node in assembly.GetCustomAttributes<MeshNodeAttribute>().Select(a => a.Node))
                meshNodes[node.Id] = node;
        }
        return Task.CompletedTask;
    }

    public Task<ArticleEntry> GetArticleAsync(string id)
    => Task.FromResult(articles.GetValueOrDefault(id));

    public Task UpdateArticleAsync(ArticleEntry article)
    {
        articles[article.Id] = article;
        return Task.CompletedTask;
    }
}
