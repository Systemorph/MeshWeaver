using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

public interface IContentService
{
    Task<Stream?> GetContentAsync(string collection, string path, CancellationToken ct = default);
    Task<IReadOnlyCollection<Article>> GetArticleCatalogAsync(ArticleCatalogOptions options, CancellationToken ct = default);
    IObservable<object?> GetArticle(string collection, string article);

    Task<IReadOnlyCollection<ContentCollection>> GetCollectionsAsync(CancellationToken ct = default);
    IReadOnlyCollection<ContentCollection> GetCollections();
    IEnumerable<ContentCollection> GetCollections(string context);
    ContentCollection? GetCollection(string collectionName);

    /// <summary>
    /// Initializes a content collection from configuration if it doesn't already exist.
    /// Creates the provider and initializes the collection.
    /// </summary>
    /// <param name="config">The configuration for the collection</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The initialized collection (existing or newly created)</returns>
    Task<ContentCollection> InitializeCollectionAsync(ContentCollectionConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the collection mapped to the specified address asynchronously.
    /// If the collection is not found locally, attempts to load it dynamically from the remote hub.
    /// Returns null if no collection is found.
    /// </summary>
    Task<IReadOnlyCollection<ContentCollection>> GetCollectionForAddressAsync(Address address,
        CancellationToken cancellationToken = default);
}
