namespace MeshWeaver.ContentCollections;

/// <summary>
/// Registry for content collections that supports hierarchical inheritance.
/// Similar to ITypeRegistry, allows parent-child hubs to share and override content collection configurations.
/// </summary>
public interface IContentCollectionRegistry
{
    /// <summary>
    /// Registers a content collection configuration.
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <param name="config">Configuration for the collection</param>
    /// <returns>The registry for fluent chaining</returns>
    IContentCollectionRegistry WithCollection(string collectionName, ContentCollectionRegistration config);

    /// <summary>
    /// Gets a content collection configuration by name, checking this registry and parent registries.
    /// </summary>
    /// <param name="collectionName">Name of the collection</param>
    /// <returns>The collection registration, or null if not found</returns>
    ContentCollectionRegistration? GetCollection(string collectionName);

    /// <summary>
    /// Gets all collection registrations from this registry and parent registries.
    /// </summary>
    IEnumerable<KeyValuePair<string, ContentCollectionRegistration>> Collections { get; }
}

/// <summary>
/// Registration information for a content collection.
/// Contains both the configuration and a factory function to create the provider.
/// </summary>
public record ContentCollectionRegistration(
    ContentCollectionConfig Config,
    Func<IServiceProvider, IStreamProvider>? ProviderFactory = null
);
