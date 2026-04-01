using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;

namespace MeshWeaver.ContentCollections;

public class ContentCollection : IDisposable
{
    private readonly ISynchronizationStream<InstanceCollection> markdownStream;
    private readonly IStreamProvider provider;
    public readonly ContentCollectionConfig Config;
    public Address? Address => Config.Address;
    private IDisposable? monitorDisposable;

    public ContentCollection(ContentCollectionConfig config, IStreamProvider provider, IMessageHub hub)
    {
        Hub = hub;
        Config = config;
        this.provider = provider;
        markdownStream = CreateStream();
    }

    private ISynchronizationStream<InstanceCollection> CreateStream()
    {
        var ret = new SynchronizationStream<InstanceCollection>(
            new(Collection, null),
        Hub,
            new EntityReference(Collection, "/"),
            Hub.CreateReduceManager().ReduceTo<InstanceCollection>(),
            x => x);
        return ret;
    }


    public IMessageHub Hub { get; }
    public string Collection => Config.Name!;
    public string DisplayName => Config.DisplayName ?? Config.Name!.Wordify();

    public IObservable<object?> GetMarkdown(string path)
        => markdownStream
            .Reduce(new InstanceReference(path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? path[..^3]
                : path.TrimStart('/')),
                c => c.ReturnNullWhenNotPresent())!
            .Select(x => x.Value);


    public Task<Stream?> GetContentAsync(string path, CancellationToken ct = default)
        => provider.GetStreamAsync(path, ct);

    /// <summary>
    /// Returns content as text/markdown. For supported binary formats (.docx, .pptx, .xlsx),
    /// converts to markdown via registered IContentTransformer. For text files, reads as-is.
    /// </summary>
    public async Task<string?> GetContentAsTextAsync(string path, IEnumerable<IContentTransformer>? transformers = null, CancellationToken ct = default)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

        // Try registered transformers first
        var transformer = transformers?.FirstOrDefault(t =>
            t.SupportedExtensions.Contains(ext));
        if (transformer != null)
        {
            var stream = await GetContentAsync(path, ct);
            if (stream == null) return null;
            using (stream)
                return await transformer.TransformToMarkdownAsync(stream, ct);
        }

        // Fallback: read as text
        var textStream = await GetContentAsync(path, ct);
        if (textStream == null) return null;
        using (textStream)
        {
            using var reader = new StreamReader(textStream);
            return await reader.ReadToEndAsync(ct);
        }
    }



    public virtual void Dispose()
    {
        monitorDisposable?.Dispose();
        markdownStream.Dispose();
    }

    public Task<IReadOnlyCollection<FolderItem>> GetFoldersAsync(string path)
        => provider.GetFoldersAsync(path);

    public Task<IReadOnlyCollection<FileItem>> GetFilesAsync(string path)
        => provider.GetFilesAsync(path);

    public Task SaveFileAsync(string path, string fileName, Stream openReadStream)
        => provider.SaveFileAsync(path, fileName, openReadStream);

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

    public virtual async Task<InstanceCollection> InitializeAsync(CancellationToken ct)
    {
        var parsedArticles = new Dictionary<object, object>();
        await foreach (var tuple in provider.GetStreamsAsync(MarkdownFilter, ct).WithCancellation(ct))
        {
            var article = await ParseArticleAsync(tuple.Stream, tuple.Path, tuple.LastModified, ct);
            if (article is not null)
            {
                var key = (object)(article.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? article.Path[..^3] : article.Path);
                parsedArticles[key] = article;
            }
        }
        var ret = new InstanceCollection(parsedArticles);
        markdownStream.OnNext(new(ret, markdownStream.StreamId, Hub.Version));
        AttachMonitor();
        return ret;
    }

    protected void UpdateArticle(string path)
    {
        markdownStream.Update(async (x, ct) =>
        {
            var tuple = await provider.GetStreamWithMetadataAsync(path, ct);
            if (tuple.Stream is null)
                return null;
            var article = await ParseArticleAsync(tuple.Stream, tuple.Path, tuple.LastModified, ct);
            if (article is null)
                return null;
            var key = article.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? article.Path[..^3] : article.Path;
            return new ChangeItem<InstanceCollection>(x!.SetItem(key, article), markdownStream.StreamId, Hub.Version);

        }, _ => Task.CompletedTask);
    }

    private async Task<MarkdownElement?> ParseArticleAsync(Stream? stream, string path, DateTime lastModified, CancellationToken ct)
    {
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(ct);


        return ContentCollectionsExtensions.ParseContent(
            Collection,
            path,
            lastModified,
            content,
            Address
        );
    }

    protected void AttachMonitor()
    {
        monitorDisposable = provider.AttachMonitor(UpdateArticle);
    }

    public Task CreateFolderAsync(string folderPath)
        => provider.CreateFolderAsync(folderPath);

    public Task DeleteFolderAsync(string folderPath)
        => provider.DeleteFolderAsync(folderPath);

    public Task DeleteFileAsync(string filePath)
        => provider.DeleteFileAsync(filePath);

    public virtual string GetContentType(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (string.IsNullOrEmpty(extension))
            return "text/markdown";
        return MimeTypes.GetValueOrDefault(extension, "application/octet-stream");
    }

    private static readonly Dictionary<string, string> MimeTypes = new()
    {
        { ".txt", "text/plain" },
        { ".md", "text/markdown" },
        { ".html", "text/html" },
        { ".htm", "text/html" },
        { ".css", "text/css" },
        { ".js", "application/javascript" },
        { ".jpg", "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".png", "image/png" },
        { ".gif", "image/gif" },
        { ".bmp", "image/bmp" },
        { ".svg", "image/svg+xml" },
        { ".webp", "image/webp" },
        { ".ico", "image/x-icon" },
        { ".json", "application/json" },
        { ".csv", "text/csv" },
        { ".pdf", "application/pdf" },
        { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
        { ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
        { ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },
        { ".woff", "font/woff" },
        { ".woff2", "font/woff2" },
        { ".ttf", "font/ttf" },
        { ".eot", "application/vnd.ms-fontobject" },
        { ".otf", "font/otf" },
    };
}
