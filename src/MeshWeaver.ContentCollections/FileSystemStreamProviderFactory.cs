namespace MeshWeaver.ContentCollections;

/// <summary>
/// Factory for creating FileSystemStreamProvider instances
/// </summary>
public class FileSystemStreamProviderFactory : IStreamProviderFactory
{
    public IStreamProvider Create(Dictionary<string, string>? configuration)
    {
        var basePath = configuration?.GetValueOrDefault("BasePath") ?? "";
        return new FileSystemStreamProvider(basePath);
    }

}
