namespace MeshWeaver.ContentCollections;

public interface IContentService
{
    Task<Stream?> GetContentAsync(string collection, string path, CancellationToken ct = default);
    Task<IReadOnlyCollection<Article>> GetArticleCatalogAsync(ArticleCatalogOptions options, CancellationToken ct = default);
    Task<IObservable<object?>> GetArticleAsync(string collection, string article, CancellationToken ct = default);

    Task<IReadOnlyCollection<ContentCollection>> GetCollectionsAsync(CancellationToken ct = default);
    Task<ContentCollection?> GetCollectionAsync(string collectionName, CancellationToken ct = default);

    /// <summary>
    /// Initializes a content collection from configuration if it doesn't already exist.
    /// Creates the provider and initializes the collection.
    /// </summary>
    /// <param name="config">The configuration for the collection</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The initialized collection (existing or newly created)</returns>
    Task<ContentCollection?> InitializeCollectionAsync(ContentCollectionConfig config, CancellationToken cancellationToken = default);


    ContentCollectionConfig GetCollectionConfig(string collection);
    void AddConfiguration(ContentCollectionConfig contentCollectionConfig);
}
