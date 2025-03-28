using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Articles;

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


    public override Task<Stream> GetContentAsync(string path, CancellationToken ct = default)
    {
        if (path is null)
            return null;
        var fullPath = Path.Combine(BasePath, path);
        if (!File.Exists(fullPath))
            return null;
        return Task.FromResult<Stream>(File.OpenRead(fullPath));
    }

    private ISynchronizationStream<InstanceCollection> CreateStream(string path)
    {
        var ret = new SynchronizationStream<InstanceCollection>(
            new(Collection, path),
            Hub,
            new EntityReference(Collection, path),
            Hub.CreateReduceManager().ReduceTo<InstanceCollection>(),
            x => x);
        ret.Initialize(InitializeAsync, null);
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
        if (Path.GetExtension(e.FullPath) == ".md")
            UpdateArticle(e.FullPath);
    }


    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (Path.GetExtension(e.FullPath) == ".md")
            UpdateArticle(e.FullPath);
    }

    private void UpdateArticle(string path)
    {
        articleStream.Update(async (x, ct) =>
        {
            var article = await LoadArticle(path, ct);
            return article is null ? null : new ChangeItem<InstanceCollection>(x.SetItem(article.Name, article), Hub.Version);
        }, null);
    }

    public async Task<InstanceCollection> InitializeAsync(CancellationToken ct)
    {
        var authorsFile = Path.Combine(BasePath, "authors.json");
        if (File.Exists(authorsFile))
            Authors = LoadAuthorsAsync(await File.ReadAllTextAsync(authorsFile, ct));

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
        if (!File.Exists(fullPath))
            return null;
        await using var stream = File.OpenRead(fullPath);
        var content = await new StreamReader(stream).ReadToEndAsync(ct);
        return ArticleExtensions.ParseArticle(Collection, Path.GetRelativePath(BasePath, fullPath), File.GetLastWriteTime(fullPath), content, Authors);
    }

    public override void Dispose()
    {
        base.Dispose();
        articleStream.Dispose();
        watcher?.Dispose();
        watcher = null;
    }

    public override Task<IReadOnlyCollection<FolderItem>> GetFoldersAsync(string path)
    {
        var fullPath = Path.Combine(BasePath, path.TrimStart('/'));

        return Task.FromResult<IReadOnlyCollection<FolderItem>>(Directory.GetDirectories(fullPath)
            .Select(dirPath =>
            {
                var itemCount = Directory.GetFileSystemEntries(dirPath).Length;
                return new FolderItem(
                    dirPath,
                    Path.GetFileName(dirPath),
                    itemCount
                );
            })
            .ToArray());
    }

    public override Task<IReadOnlyCollection<FileItem>> GetFilesAsync(string path)
    {
        var fullPath = Path.Combine(BasePath, path.TrimStart('/'));

        return Task.FromResult<IReadOnlyCollection<FileItem>>(Directory.GetFiles(fullPath)
            .Select(filePath =>
            {
                var fileInfo = new FileInfo(filePath);

                return new FileItem(
                    filePath,
                    Path.GetFileName(filePath),
                    fileInfo.LastWriteTime
                );
            })
            .ToArray());
    }
    public override async Task SaveFileAsync(string path, string fileName, Stream openReadStream)
    {
        var fullPath = Path.Combine(path, fileName);
        await using var fileStream =
            new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.Write);
        await openReadStream.CopyToAsync(fileStream);
    }

    public override Task CreateFolderAsync(string path)
    {
        Directory.CreateDirectory(Path.Combine(BasePath, path));
        return Task.CompletedTask;
    }
}
