namespace MeshWeaver.ContentCollections;

public interface IContentService
{
    Task<Stream?> GetContentAsync(string collection, string path, CancellationToken ct = default);
    Task<IReadOnlyCollection<Article>> GetArticleCatalogAsync(ArticleCatalogOptions options, CancellationToken ct = default);
    Task<IObservable<object?>> GetArticleAsync(string collection, string article, CancellationToken ct = default);

    IAsyncEnumerable<ContentCollection> GetCollectionsAsync();
    Task<ContentCollection?> GetCollectionAsync(string collectionName, CancellationToken ct = default);

    ContentCollectionConfig? GetCollectionConfig(string collection);
    void AddConfiguration(ContentCollectionConfig contentCollectionConfig);
}
