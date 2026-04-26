using System.Reactive.Linq;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Factory for creating FileSystemStreamProvider instances
/// </summary>
public class FileSystemStreamProviderFactory : IStreamProviderFactory
{
    public IObservable<IStreamProvider> Create(ContentCollectionConfig config)
    {
        var basePath = config.BasePath ?? config.Settings?.GetValueOrDefault("BasePath") ?? "";
        return Observable.Return<IStreamProvider>(new FileSystemStreamProvider(basePath));
    }
}
