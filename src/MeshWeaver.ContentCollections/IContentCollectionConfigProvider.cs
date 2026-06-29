namespace MeshWeaver.ContentCollections;

/// <summary>
/// Defines a contract for retrieving collections of content.
/// </summary>
/// <remarks>Implementations of this interface provide a mechanism to retrieve one or more  <see
/// cref="ContentCollection"/> instances. The specific source and nature of the content  collections depend on the
/// implementation.</remarks>
public interface IContentCollectionConfigProvider
{
    /// <summary>
    /// Implementations should return one or more content collections.
    /// </summary>
    IEnumerable<ContentCollectionConfig> GetCollections();
}

/// <summary>
/// Simple <see cref="IContentCollectionConfigProvider"/> that returns a fixed set of
/// configurations supplied at construction.
/// </summary>
/// <param name="collections">The collection configurations to expose.</param>
public class ContentCollectionConfigProvider(params IEnumerable<ContentCollectionConfig> collections) : IContentCollectionConfigProvider
{
    /// <inheritdoc />
    public IEnumerable<ContentCollectionConfig> GetCollections() => collections;
}
