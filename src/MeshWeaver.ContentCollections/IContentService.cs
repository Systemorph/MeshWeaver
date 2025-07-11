namespace MeshWeaver.ContentCollections;

public interface IContentService
{
    Task<Stream?> GetContentAsync(string collection, string path, CancellationToken ct = default);
    Task<IReadOnlyCollection<Article>> GetArticleCatalog(ArticleCatalogOptions options, CancellationToken ct = default);
    IObservable<object?> GetArticle(string collection, string article);

    Task<IReadOnlyCollection<ContentCollection>> GetCollectionsAsync(CancellationToken ct = default);
    IReadOnlyCollection<ContentCollection> GetCollections(bool includeHidden = false);
    IReadOnlyCollection<ContentCollection> GetCollections(string context);
    ContentCollection? GetCollection(string collectionName);
}
