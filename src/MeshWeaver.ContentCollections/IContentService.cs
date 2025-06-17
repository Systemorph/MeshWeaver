namespace MeshWeaver.ContentCollections;

public interface IContentService
{
    Task<Stream> GetContentAsync(string collection, string path, CancellationToken ct = default);
    Task<IReadOnlyCollection<Article>> GetArticleCatalog(ArticleCatalogOptions options, CancellationToken ct = default);
    IObservable<object> GetArticle(string collection, string article);

    Task<IReadOnlyCollection<ContentCollection>> GetCollectionsAsync(CancellationToken ct = default);
    ContentCollection GetCollection(string collectionName);
}
