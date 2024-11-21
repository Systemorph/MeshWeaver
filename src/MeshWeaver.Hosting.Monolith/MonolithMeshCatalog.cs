using System.Collections.Concurrent;
using System.Reflection;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Monolith;

public class MonolithMeshCatalog(IMessageHub hub) : IMeshCatalog
{
    private readonly ILogger<MonolithMeshCatalog> logger = hub.ServiceProvider.GetRequiredService<ILogger<MonolithMeshCatalog>>();
    private readonly ConcurrentDictionary<string, MeshNode> meshNodes = new();
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, MeshArticle>> articles = new();
    private MeshConfiguration Configuration { get; } = hub.Configuration.GetMeshContext();


    public Task<MeshNode> GetNodeAsync(string id)
        => Task.FromResult(meshNodes.GetValueOrDefault(id));
    public Task UpdateAsync(MeshNode node)
    {
        meshNodes[node.Id] = node;
        // TODO V10: Delegate indexing to IMeshIndexService running on its own hub. (06.09.2024, Roland Bürgi)
        return Task.CompletedTask;
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (var assemblyLocation in Configuration.InstallAtStartup)
        {
            var assembly = Assembly.LoadFrom(assemblyLocation);
            foreach (var node in assembly.GetCustomAttributes<MeshNodeAttribute>().Select(a => a.Node))
            {
                meshNodes[node.Id] = node;
            }
        }
        return Task.CompletedTask;
    }

    private async IAsyncEnumerable<MeshArticle> InitializeArticlesForNodeAsync(MeshNode node)
    {
        var files = Directory.GetFiles(Path.Combine(node.BasePath, node.ArticlePath));
        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var extension = Path.GetExtension(file);
            await using var stream = File.OpenRead(file);
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();

            var article = ParseArticle(fileName, extension, content);
            yield return article;
        }
    }

    private const string md = nameof(md);
    private MeshArticle ParseArticle(string fileName, string extension, string content)
    {
        return null;
        //return extension.ToLowerInvariant() switch
        //{
        //    md =>  
        //}
    }

    public async Task<MeshArticle> GetArticleAsync(string application, string id, bool includeContent)
    {
        if (articles.TryGetValue(id, out var inner))
            return await IncludeContent(inner.GetValueOrDefault(id), includeContent);
        var node = await GetNodeAsync(application);
        var articlesByApplication = await InitializeArticlesForNodeAsync(node).ToDictionaryAsync(x => x.Name);
        articles[application] = articlesByApplication;
        return await IncludeContent(articlesByApplication.GetValueOrDefault(id), includeContent);
    }


    private static Task<MeshArticle> IncludeContent(MeshArticle article, bool includeContent)
    {
        if(includeContent || article is null)
            return Task.FromResult(article);
        return Task.FromResult(article with { Content = null });
    }

    public Task UpdateArticleAsync(MeshArticle meshArticle)
    {
        throw new NotImplementedException();
    }
}
