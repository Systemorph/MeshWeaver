namespace MeshWeaver.ContentCollections;

/// <summary>
/// Defines a contract for retrieving collections of content.
/// </summary>
/// <remarks>Implementations of this interface provide a mechanism to retrieve one or more  <see
/// cref="ContentCollection"/> instances. The specific source and nature of the content  collections depend on the
/// implementation.</remarks>
public interface IContentCollectionProvider
{
    /// <summary>
    /// Implementations should return one or more content collections.
    /// </summary>
    IEnumerable<ContentCollection> GetCollections();
}

public class ContentCollectionProvider(params IEnumerable<ContentCollection> collections) : IContentCollectionProvider
{
    public IEnumerable<ContentCollection> GetCollections() => collections;
}
