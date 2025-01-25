using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;

namespace MeshWeaver.Articles;

public abstract record ArticleCollection(string Collection)
{
    public string DisplayName { get; init; } = Collection.Wordify();
    public Icon Icon { get; init; }

    public string DefaultAddress { get; init; }
    public abstract IObservable<Article> GetArticle(string path, ArticleOptions options = null);

    public abstract IObservable<IEnumerable<Article>> GetArticles(ArticleCatalogOptions toOptions);

    public abstract void Initialize(IMessageHub hub);

    public abstract Task<byte[]> GetContentAsync(string path, CancellationToken ct = default);
}
public abstract record ArticleCollection<TCollection>(string Collection) : ArticleCollection(Collection)
    where TCollection:ArticleCollection<TCollection>
{
    protected TCollection This => (TCollection)this;
    public TCollection WithDisplayName(string displayName)
        => This with { DisplayName = displayName };

    public TCollection WithIcon(Icon icon)
        => This with { Icon = icon };
    public TCollection WithDefaultAddress(string address)
        => This with { DefaultAddress = address };
}

public record FileSystemCollection(string Collection, string BasePath) : ArticleCollection<FileSystemCollection>(Collection)
{
    private readonly ConcurrentDictionary<string, ISynchronizationStream<Article>> articleStreams = new();
    private IMessageHub Hub { get; set; }
    public override IObservable<IEnumerable<Article>> GetArticles(ArticleCatalogOptions toOptions)
    => articleStreams.Values.Select(x => x.Select(y => y.Value))
        .CombineLatest()
        // TODO V10: Consider options here to steer query. (24.01.2025, Roland Bürgi)
        .Select(x => x.OrderByDescending(a => a.Published))
        ;

    public override void Initialize(IMessageHub hub)
    {
        if (articleStreams.Count > 0)
            return;
        var files = Directory.GetFiles(BasePath, "*.md");
        foreach (var file in files)
        {
            Hub = hub;
            var name = Path.GetFileNameWithoutExtension(file); // Exclude extension
            var stream = articleStreams.GetOrAdd(name, CreateStream);
            LoadAndSet(file, stream);
        }
        MonitorFileSystem();
    }

    public override async Task<byte[]> GetContentAsync(string path, CancellationToken ct)
    {
        if (path is null)
            return null;
        var fullPath = Path.Combine(BasePath, path);
        if(!File.Exists(fullPath))
            return null;
        return await File.ReadAllBytesAsync(fullPath, ct);

    }

    private ISynchronizationStream<Article> CreateStream(string path)
    {
        return new SynchronizationStream<Article>(
            new(Hub.Address, path), 
            Hub, 
            new EntityReference(Collection, path),
            null,
            x => x);
    }

    public override IObservable<Article> GetArticle(string path, ArticleOptions options = null)
    {
        var ret = articleStreams.GetValueOrDefault(path);
        if (ret is null)
            return null;
        if (options is not null && (!options.ContentIncluded || !options.PrerenderIncluded))
            return ret.Select(art => art.Value with
            {
                Content = options.ContentIncluded ? art.Value.Content : null,
                PrerenderedHtml = options.PrerenderIncluded ? art.Value.PrerenderedHtml : null
            });
        return ret.Select(x => x.Value);
    }

    public void MonitorFileSystem()
    {
        var watcher = new FileSystemWatcher(BasePath);
        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
        watcher.Changed += OnChanged;
        watcher.EnableRaisingEvents = true;
    }


    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var path = Path.GetRelativePath(BasePath, e.FullPath);
        var s = articleStreams.GetOrAdd(path, CreateStream);
        LoadAndSet(e.FullPath, s);
    }

    private void LoadAndSet(string fullPath, ISynchronizationStream<Article> s)
    {
        s.UpdateAsync(async (_, ct) => new ChangeItem<Article>(await LoadArticle(fullPath, ct), Hub.Address, ChangeType.Full, Hub.Version, null));
    }

    private async Task<Article> LoadArticle(string fullPath, CancellationToken ct)
    {
        if(!File.Exists(fullPath))
           return null;
        await using var stream = File.OpenRead(fullPath);
        var content = await new StreamReader(stream).ReadToEndAsync(ct);
        return ArticleExtensions.ParseArticle(Collection, DefaultAddress, Path.GetRelativePath(BasePath, fullPath), File.GetLastWriteTime(fullPath),content);
    }
}
