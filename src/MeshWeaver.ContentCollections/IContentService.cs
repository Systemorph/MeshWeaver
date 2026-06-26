namespace MeshWeaver.ContentCollections;

/// <summary>
/// Provides access to content collections and their configurations for a hub, including
/// reading raw file content and resolving collections from the local hub or its parents.
/// </summary>
public interface IContentService
{
    /// <summary>Opens a read stream for a file within a collection.</summary>
    /// <param name="collection">The collection name.</param>
    /// <param name="path">The file path within the collection.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A readable stream, or <c>null</c> when the file does not exist.</returns>
    Task<Stream?> GetContentAsync(string collection, string path, CancellationToken ct = default);

    /// <summary>Enumerates the content collections currently instantiated on this service.</summary>
    /// <returns>An async sequence of live collections.</returns>
    IAsyncEnumerable<ContentCollection> GetCollectionsAsync();

    /// <summary>Resolves (and lazily instantiates) a collection by name, falling back to parent services.</summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The collection, or <c>null</c> when it cannot be found.</returns>
    Task<ContentCollection?> GetCollectionAsync(string collectionName, CancellationToken ct = default);

    /// <summary>Gets the configuration for a collection (resolving mapped configs), or <c>null</c> if unknown.</summary>
    /// <param name="collection">The collection name.</param>
    /// <returns>The resolved configuration, or <c>null</c>.</returns>
    ContentCollectionConfig? GetCollectionConfig(string collection);

    /// <summary>Gets all collection configurations visible to children (local plus inherited from parents).</summary>
    /// <returns>The visible collection configurations.</returns>
    IReadOnlyCollection<ContentCollectionConfig> GetAllCollectionConfigs();

    /// <summary>Adds or replaces a collection configuration, invalidating any cached collection whose base path changed.</summary>
    /// <param name="contentCollectionConfig">The configuration to register.</param>
    void AddConfiguration(ContentCollectionConfig contentCollectionConfig);
}
