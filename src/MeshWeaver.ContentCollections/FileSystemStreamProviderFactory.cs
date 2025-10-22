namespace MeshWeaver.ContentCollections;

/// <summary>
/// Factory for creating FileSystemStreamProvider instances
/// </summary>
public class FileSystemStreamProviderFactory : IStreamProviderFactory
{
    public IStreamProvider Create(ContentCollectionConfig config)
    {
        var basePath = config.BasePath ?? config.Settings?.GetValueOrDefault("BasePath") ?? "";
        return new FileSystemStreamProvider(basePath);
    }

}
