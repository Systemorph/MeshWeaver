using System.Reactive.Linq;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Factory for creating FileSystemStreamProvider instances
/// </summary>
public class FileSystemStreamProviderFactory : IStreamProviderFactory
{
    /// <summary>
    /// Creates a <see cref="FileSystemStreamProvider"/> rooted at the config's <c>BasePath</c>
    /// (or the <c>BasePath</c> setting, falling back to the empty/current directory).
    /// </summary>
    /// <param name="config">The collection configuration supplying the base path.</param>
    /// <returns>An observable that emits the constructed stream provider.</returns>
    public IObservable<IStreamProvider> Create(ContentCollectionConfig config)
    {
        var basePath = config.BasePath ?? config.Settings?.GetValueOrDefault("BasePath") ?? "";
        return Observable.Return<IStreamProvider>(new FileSystemStreamProvider(basePath));
    }
}
