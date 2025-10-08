using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text;
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
            x => x.WithInitialization((_, ct) => InitializeAsync(ct)));
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


    public IObservable<IEnumerable<object>> GetMarkdown(ArticleCatalogOptions _)
        => markdownStream.Where(x => x.Value != null)
            .Select(x => x.Value!.Instances.Values);


    public Task<Stream?> GetContentAsync(string path, CancellationToken ct = default)
        => provider.GetStreamAsync(path, ct);

    public async Task<GetContentResponse> GetContentResponseAsync(string path, CancellationToken ct = default)
    {
        var contentType = GetContentType(path);
        var fileName = Path.GetFileName(path);

        // Get stream from provider
        var stream = await provider.GetStreamAsync(path, ct);
        if (stream == null)
        {
            return new GetContentResponse(null, null);
        }

        // For embedded resources, always inline content
        if (provider.ProviderType == "EmbeddedResource")
        {
            using (stream)
            {
                if (IsTextContent(contentType))
                {
                    using var reader = new StreamReader(stream);
                    var content = await reader.ReadToEndAsync(ct);
                    return new GetContentResponse(contentType, fileName)
                    {
                        SourceType = GetContentResponse.InlineSourceType,
                        InlineContent = content
                    };
                }
                else
                {
                    using var memoryStream = new MemoryStream();
                    await stream.CopyToAsync(memoryStream, ct);
                    var base64Content = Convert.ToBase64String(memoryStream.ToArray());
                    return new GetContentResponse(contentType, fileName)
                    {
                        SourceType = GetContentResponse.InlineSourceType,
                        InlineContent = base64Content
                    };
                }
            }
        }

        // For FileSystem with small text files, inline them
        if (provider.ProviderType == "FileSystem" && Config.BasePath != null)
        {
            var fullPath = Path.Combine(Config.BasePath, path.TrimStart('/'));
            if (ShouldInlineContent(contentType, fullPath))
            {
                using (stream)
                using (var reader = new StreamReader(stream))
                {
                    var content = await reader.ReadToEndAsync(ct);
                    return new GetContentResponse(contentType, fileName)
                    {
                        SourceType = GetContentResponse.InlineSourceType,
                        InlineContent = content
                    };
                }
            }
        }

        // For all other cases, return provider reference
        stream.Dispose();
        var providerReference = GetProviderReference(path);
        return new GetContentResponse(contentType, fileName)
        {
            SourceType = provider.ProviderType,
            ProviderName = Collection,
            ProviderReference = providerReference
        };
    }

    private string? GetProviderReference(string path)
    {
        return provider.ProviderType switch
        {
            "EmbeddedResource" => GetEmbeddedResourceName(path),
            _ => Path.Combine(Config.BasePath!, path.TrimStart('/'))
        };
    }

    private string? GetEmbeddedResourceName(string path)
    {
        if (provider is EmbeddedResourceStreamProvider embeddedProvider)
        {
            return embeddedProvider.GetResourceName(path);
        }
        return null;
    }

    private static bool IsTextContent(string contentType)
    {
        var textTypes = new[]
        {
            "text/css",
            "application/javascript",
            "text/html",
            "application/json",
            "text/plain",
            "image/svg+xml"
        };

        return textTypes.Contains(contentType) || contentType.StartsWith("text/");
    }

    private static bool ShouldInlineContent(string contentType, string filePath)
    {
        if (!IsTextContent(contentType))
        {
            return false;
        }

        var fileInfo = new FileInfo(filePath);
        return fileInfo.Exists && fileInfo.Length < 100_000;
    }

    public virtual void Dispose()
    {
        monitorDisposable?.Dispose();
        markdownStream.Dispose();
    }

    protected ImmutableDictionary<string, Author> Authors { get; private set; } = ImmutableDictionary<string, Author>.Empty;

    protected ImmutableDictionary<string, Author> ParseAuthors(string content)
    {
        var ret = JsonSerializer
            .Deserialize<ImmutableDictionary<string, Author>>(
                content
            );
        return ret?.Select(x =>
            new KeyValuePair<string, Author>(
                x.Key,
                x.Value with
                {
                    ImageUrl = x.Value.ImageUrl is null ? null : $"static/{Collection}/{x.Value.ImageUrl}"
                })).ToImmutableDictionary() ?? ImmutableDictionary<string, Author>.Empty;
    }

    public Task<IReadOnlyCollection<FolderItem>> GetFoldersAsync(string path)
        => provider.GetFoldersAsync(path);

    public Task<IReadOnlyCollection<FileItem>> GetFilesAsync(string path)
        => provider.GetFilesAsync(path);

    public Task SaveFileAsync(string path, string fileName, Stream openReadStream)
        => provider.SaveFileAsync(path, fileName, openReadStream);

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

    public virtual async Task<InstanceCollection> InitializeAsync(CancellationToken ct)
    {
        Authors = await provider.LoadAuthorsAsync(ct);
        var ret = new InstanceCollection(
            await provider.GetStreamsAsync(MarkdownFilter, ct)
                .SelectAwait(async tuple => await ParseArticleAsync(tuple.Stream, tuple.Path, tuple.LastModified, ct))
                .Where(x => x is not null)
                .ToDictionaryAsync(x => (object)(x!.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? x.Path[..^3] : x.Path), x => (object)x!, cancellationToken: ct)
        );
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
            Authors
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
        { ".pdf", "application/pdf" },
        { ".woff", "font/woff" },
        { ".woff2", "font/woff2" },
        { ".ttf", "font/ttf" },
        { ".eot", "application/vnd.ms-fontobject" },
        { ".otf", "font/otf" },
    };
}
