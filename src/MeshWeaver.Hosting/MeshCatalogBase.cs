using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting;

public abstract class MeshCatalogBase(MeshConfiguration configuration) : IMeshCatalog
{
    private MeshConfiguration Configuration { get; } = configuration;


    public async Task<MeshNode> GetNodeAsync(string addressType, string id)
        => 
            Configuration.Nodes.GetValueOrDefault((addressType, id))
            ??
            Configuration.MeshNodeFactories
                .Select(f => f.Invoke(addressType, id))
                .FirstOrDefault(x => x != null)
            ?? 
            await LoadMeshNode(addressType, id)
    ;

    protected abstract Task<MeshNode> LoadMeshNode(string addressType, string id);

    public abstract Task UpdateAsync(MeshNode node);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (var assemblyLocation in Configuration.InstallAtStartup)
        {
            var assembly = Assembly.LoadFrom(assemblyLocation);
            foreach (var node in assembly.GetCustomAttributes<MeshNodeAttribute>().SelectMany(a => a.Nodes))
                await InitializeNodeAsync(node);
        }
    }

    protected abstract Task InitializeNodeAsync(MeshNode node);

    protected async IAsyncEnumerable<MeshArticle> InitializeArticlesForNodeAsync(MeshNode node)
    {
        var files = Directory.GetFiles(Path.Combine(node.PackageName, node.ArticlePath));
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

    public abstract Task<MeshArticle>
        GetArticleAsync(string addressType, string nodeId, string id, bool includeContent);



    protected static Task<MeshArticle> IncludeContent(MeshArticle article, bool includeContent)
    {
        if (includeContent || article is null)
            return Task.FromResult(article);
        return Task.FromResult(article with { Content = null });
    }

    public Task UpdateArticleAsync(MeshArticle meshArticle)
    {
        throw new NotImplementedException();
    }

}
