using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;

namespace MeshWeaver.Articles;
public abstract class ArticleCollection(ArticleSourceConfig config, IMessageHub hub) : IDisposable
{
    protected IMessageHub Hub { get; } = hub;
    public string Collection { get; } = config.Name;
    public string DisplayName { get; } = config.DisplayName ?? config.Name.Wordify();

    public abstract IObservable<Article> GetArticle(string path, ArticleOptions options = null);

    public abstract IObservable<IEnumerable<Article>> GetArticles(ArticleCatalogOptions toOptions);

    public abstract Task<Stream> GetContentAsync(string path, CancellationToken ct = default);

    public virtual void Dispose()
    {
    }
    protected ImmutableDictionary<string, Author> Authors = ImmutableDictionary<string, Author>.Empty;

    protected ImmutableDictionary<string, Author> LoadAuthorsAsync(string content)
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

    public async Task<IReadOnlyCollection<CollectionItem>> GetCollectionItemsAsync(string currentPath)
    {
        var files = await GetFilesAsync(currentPath);
        var folders = await GetFoldersAsync(currentPath);
        return folders
            .Cast<CollectionItem>()
            .Concat(files)
            .ToArray();
    }
}

public class FileSystemArticleCollectionFactory(IMessageHub hub) : IArticleCollectionFactory
{
    public const string SourceType = "FileSystem";
    public ArticleCollection Create(ArticleSourceConfig config)
    {
        return new FileSystemArticleCollection(config, hub);
    }
}
