namespace MeshWeaver.ContentCollections;

/// <summary>
/// Factory for creating stream providers from configuration
/// </summary>
public interface IStreamProviderFactory
{
    /// <summary>
    /// Creates a stream provider from the given configuration
    /// </summary>
    /// <param name="config">Content collection configuration</param>
    /// <returns>The created stream provider</returns>
    IStreamProvider Create(ContentCollectionConfig config);
}
