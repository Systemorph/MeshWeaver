namespace MeshWeaver.ContentCollections;

/// <summary>
/// Factory for creating FileSystemStreamProvider instances
/// </summary>
public class FileSystemStreamProviderFactory : IStreamProviderFactory
{
    public Task<IStreamProvider> CreateAsync(ContentCollectionConfig config, CancellationToken cancellationToken = default)
    {
        var basePath = config.BasePath ?? config.Settings?.GetValueOrDefault("BasePath") ?? "";
        return Task.FromResult<IStreamProvider>(new FileSystemStreamProvider(basePath));
    }

}
