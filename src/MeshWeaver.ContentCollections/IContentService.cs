namespace MeshWeaver.ContentCollections;

/// <summary>
/// Provides access to content collections and their configurations for a hub, including
/// reading raw file content and resolving collections from the local hub or its parents.
/// The whole surface is reactive: every method that touches the backing store returns a
/// (replayed) <see cref="IObservable{T}"/> whose I/O leaf runs on the appropriate
/// <c>IIoPool</c>, never on the subscriber's thread.
/// </summary>
public interface IContentService
{
    /// <summary>Opens a read stream for a file within a collection.</summary>
    /// <param name="collection">The collection name.</param>
    /// <param name="path">The file path within the collection.</param>
    /// <returns>A single-emission observable of the readable stream, or <c>null</c> when the file does not exist; errors when the collection is unknown.</returns>
    IObservable<Stream?> GetContent(string collection, string path);

    /// <summary>Emits the content collections whose initialization has been requested on this service.</summary>
    /// <returns>An observable sequence of the live collections (each entry replays its one-shot initialization).</returns>
    IObservable<ContentCollection> GetCollections();

    /// <summary>Resolves (and lazily instantiates) a collection by name, falling back to parent services.</summary>
    /// <param name="collectionName">The collection name.</param>
    /// <returns>A single-emission observable of the collection, or <c>null</c> when it cannot be found.</returns>
    IObservable<ContentCollection?> GetCollection(string collectionName);

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
