using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;

namespace MeshWeaver.ContentCollections;

public abstract class ContentCollection : IDisposable
{
    private readonly ISynchronizationStream<InstanceCollection> markdownStream;
    private readonly ContentSourceConfig config;

    protected ContentCollection(ContentSourceConfig config, IMessageHub hub)
    {
        Hub = hub;
        this.config = config;
        markdownStream = CreateStream();
    }

    private  ISynchronizationStream<InstanceCollection> CreateStream()
    {
        var ret = new SynchronizationStream<InstanceCollection>(
            new(Collection, null),
        Hub,
            new EntityReference(Collection, "/"),
            Hub.CreateReduceManager().ReduceTo<InstanceCollection>(),
            x => x);
        ret.Initialize(InitializeAsync, null);
        return ret;
    }
    protected IMessageHub Hub { get; } 
    public string Collection  => config.Name;
    public string DisplayName  => config.DisplayName ?? config.Name.Wordify();

    public IObservable<object> GetMarkdown(string path)
        => markdownStream.Reduce(new InstanceReference(Path.GetFileNameWithoutExtension(path.TrimStart('/'))), c => c.ReturnNullWhenNotPresent()).Select(x => x?.Value);


    public IObservable<IEnumerable<object>> GetMarkdown(ArticleCatalogOptions _)
        => markdownStream.Select(x => x.Value.Instances.Values);


    public abstract Task<Stream> GetContentAsync(string path, CancellationToken ct = default);

    public virtual void Dispose()
    {
        markdownStream.Dispose();
    }

    protected ImmutableDictionary<string, Author> Authors { get; private set; } = ImmutableDictionary<string, Author>.Empty;

    protected ImmutableDictionary<string, Author> ParseAuthors(string content)
    {
        var ret = JsonSerializer
            .Deserialize<ImmutableDictionary<string, Author>>(
                content
            );
        return ret.Select(x =>
            new KeyValuePair<string, Author>(
                x.Key,
                x.Value with
                {
                    ImageUrl = x.Value.ImageUrl is null ? null : $"static/{Collection}/{x.Value.ImageUrl}"
                })).ToImmutableDictionary();
    }

    public abstract Task<IReadOnlyCollection<FolderItem>> GetFoldersAsync(string path);

    public abstract Task<IReadOnlyCollection<FileItem>> GetFilesAsync(string path);
    public abstract Task SaveFileAsync(string path, string fileName, Stream openReadStream);

    public async Task SaveArticleAsync(Article article)
    {
        var markdown = article.ConvertToMarkdown();
        var utfEncoding = new UTF8Encoding(false);
        await using var memoryStream = new MemoryStream(utfEncoding.GetBytes(markdown));
        await SaveFileAsync(article.Path, "", memoryStream);
    }

    public async Task<IReadOnlyCollection<CollectionItem>> GetCollectionItemsAsync(string currentPath)
    {
        var files = await GetFilesAsync(currentPath);
        var folders = await GetFoldersAsync(currentPath);
        return folders
            .Cast<CollectionItem>()
            .Concat(files)
            .ToArray();
    }
    protected static bool MarkdownFilter(string name)
        => name.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    public async Task<InstanceCollection> InitializeAsync(CancellationToken ct)
    {
        Authors = await LoadAuthorsAsync(ct);
        var ret = new InstanceCollection(
            await GetStreams(MarkdownFilter, ct)
                .SelectAwait(async tuple => await ParseArticleAsync(tuple.Stream,tuple.Path, tuple.LastModified, ct))
                .Where(x => x is not null)
                .ToDictionaryAsync(x => (object)Path.GetFileNameWithoutExtension(x.Path), x => (object)x, cancellationToken: ct)
        );
        AttachMonitor();
        return ret;
    }
    protected void UpdateArticle(string path)
    {
        markdownStream.Update(async (x, ct) =>
        {
            var tuple = await GetStreamAsync(path, ct);
            if (tuple.Stream is null)
                return null;
            var article = await ParseArticleAsync(tuple.Stream, tuple.Path, tuple.LastModified, ct);
            return article is null ? null : new ChangeItem<InstanceCollection>(x.SetItem(article.Name, article), Hub.Version);
        }, null);
    }

    protected abstract Task<(Stream Stream, string Path, DateTime LastModified)> GetStreamAsync(string path, CancellationToken ct);

    private async Task<MarkdownElement> ParseArticleAsync(Stream stream, string path, DateTime lastModified, CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(ct);
 

        return MarkdownExtensions.ParseContent(
            Collection,
            path,
            lastModified,
            content,
            Authors
        );
    }

    protected abstract void AttachMonitor();

    protected abstract Task<ImmutableDictionary<string, Author>> LoadAuthorsAsync(CancellationToken ct);

    public abstract Task CreateFolderAsync(string returnValue);

    public abstract Task DeleteFolderAsync(string folderPath);

    public abstract Task DeleteFileAsync(string filePath);
    protected abstract IAsyncEnumerable<(Stream Stream, string Path, DateTime LastModified)> GetStreams(Func<string, bool> filter, CancellationToken ct);



    public virtual string GetContentType(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if(string.IsNullOrEmpty(extension))
            return "text/markdown";
        return MimeTypes.GetValueOrDefault(extension, "application/octet-stream");
    }
    private static readonly Dictionary<string, string> MimeTypes = new()
    {
        { ".txt", "text/plain" },
        { ".md", "text/markdown" },
        { ".html", "text/html" },
        { ".htm", "text/html" },
        { ".jpg", "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".png", "image/png" },
        { ".gif", "image/gif" },
        { ".bmp", "image/bmp" },
        { ".svg", "image/svg+xml" },
        { ".json", "application/json" },
        { ".pdf", "application/pdf" },
        // Add more mappings as needed
    };
}
