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
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created stream provider</returns>
    Task<IStreamProvider> CreateAsync(ContentCollectionConfig config, CancellationToken cancellationToken = default);
}
