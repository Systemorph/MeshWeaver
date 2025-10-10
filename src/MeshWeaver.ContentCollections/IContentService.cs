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
    /// Gets a collection configuration, creating a localized version if needed.
    /// If the collection doesn't exist locally, it looks up the parent registry for the base configuration
    /// and creates a localized version with the specified name and subpath.
    /// </summary>
    /// <param name="baseCollectionName">The base collection name (e.g., "Submissions")</param>
    /// <param name="localizedCollectionName">The localized collection name (e.g., "Submissions-Microsoft-2026")</param>
    /// <param name="subPath">Optional subpath to append to the base path</param>
    /// <returns>The collection configuration, or null if not found</returns>
    ContentCollectionConfig? GetOrCreateCollectionConfig(string baseCollectionName, string localizedCollectionName, string? subPath = null);

    /// <summary>
    /// Gets a collection by name, initializing it from the specified address if it doesn't exist locally.
    /// This method is thread-safe and prevents duplicate initializations of the same collection.
    /// </summary>
    /// <param name="collectionName">The collection name to retrieve</param>
    /// <param name="address">The address to query if the collection doesn't exist locally</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The collection, or null if not found</returns>
    Task<ContentCollection?> GetOrInitializeCollectionAsync(string collectionName, Address address, CancellationToken cancellationToken = default);
}
