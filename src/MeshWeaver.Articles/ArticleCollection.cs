using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;

namespace MeshWeaver.Articles;

public abstract class ArticleCollection(ArticleSourceConfig config, IMessageHub hub) : IDisposable
{
    protected IMessageHub Hub { get; } = hub;
    public string Collection { get; } = config.Name;
    public string DisplayName { get;  } = config.DisplayName ?? config.Name.Wordify();

    public abstract IObservable<Article> GetArticle(string path, ArticleOptions options = null);

    public abstract IObservable<IEnumerable<Article>> GetArticles(ArticleCatalogOptions toOptions);


    public abstract Task<byte[]> GetContentAsync(string path, CancellationToken ct = default);

    public virtual void Dispose()
    {
    }


}

public class FileSystemArticleCollection : ArticleCollection
{
    public string BasePath { get; }
    private FileSystemWatcher watcher;

    private readonly ISynchronizationStream<InstanceCollection> articleStream;

    public FileSystemArticleCollection(ArticleSourceConfig config, IMessageHub hub) : base(config, hub)
    {
        BasePath = config.BasePath;
        articleStream = CreateStream(BasePath);
    }


    public override IObservable<IEnumerable<Article>> GetArticles(ArticleCatalogOptions toOptions) => 
        articleStream.Select(x => x.Value.Instances.Values.Cast<Article>());

    public override async Task<byte[]> GetContentAsync(string path, CancellationToken ct = default)
    {
        if (path is null)
            return null;
        var fullPath = Path.Combine(BasePath, path);
        if(!File.Exists(fullPath))
            return null;
        return await File.ReadAllBytesAsync(fullPath, ct);

    }

    private ISynchronizationStream<InstanceCollection> CreateStream(string path)
    {
        var ret = new SynchronizationStream<InstanceCollection>(
            new(Collection, path), 
            Hub, 
            new EntityReference(Collection, path),
            Hub.CreateReduceManager().ReduceTo<InstanceCollection>(),
            x => x);
        ret.Initialize(InitializeAsync);
        return ret;
    }

    public override IObservable<Article> GetArticle(string path, ArticleOptions options = null) => 
        articleStream.Reduce(new InstanceReference(path), c => c.ReturnNullWhenNotPresent()).Select(x => (Article)x?.Value);

    public void MonitorFileSystem()
    {
        watcher = new FileSystemWatcher(BasePath)
        {
            IncludeSubdirectories = true
        };
        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
        watcher.Changed += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.EnableRaisingEvents = true;
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        UpdateArticle(e.FullPath);
    }


    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        UpdateArticle(e.FullPath);
    }

    private void UpdateArticle(string path)
    {
        articleStream.UpdateAsync(async (x, ct) =>
        {
            var article = await LoadArticle(path, ct);
            return article is null ? null : new ChangeItem<InstanceCollection>(x.SetItem(article.Name, article), Hub.Version);
        });
    }

    public async Task<InstanceCollection> InitializeAsync(CancellationToken ct)
    {
        var ret = new InstanceCollection(
            await GetAllFromPath()
                .ToDictionaryAsync(x => (object)x.Name, x => (object)x, cancellationToken: ct)
        );
        MonitorFileSystem();
        return ret;
    }

    private async IAsyncEnumerable<Article> GetAllFromPath()
    {
        var files = Directory.GetFiles(BasePath, "*.md");
        foreach (var file in files)
        {
            yield return await LoadArticle(file, CancellationToken.None);
        }
    }


    private async Task<Article> LoadArticle(string fullPath, CancellationToken ct)
    {
        if(!File.Exists(fullPath))
           return null;
        await using var stream = File.OpenRead(fullPath);
        var content = await new StreamReader(stream).ReadToEndAsync(ct);
        return ArticleExtensions.ParseArticle(Collection, Path.GetRelativePath(BasePath, fullPath), File.GetLastWriteTime(fullPath),content);
    }

    public override void Dispose()
    {
        base.Dispose();
        articleStream.Dispose();
        watcher?.Dispose();
        watcher = null;
    }
}
