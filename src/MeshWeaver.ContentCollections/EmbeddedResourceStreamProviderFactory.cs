using System.Reactive.Linq;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Factory for creating EmbeddedResourceStreamProvider instances
/// </summary>
public class EmbeddedResourceStreamProviderFactory : IStreamProviderFactory
{
    /// <summary>
    /// Creates an <see cref="EmbeddedResourceStreamProvider"/> from the config's
    /// <c>AssemblyName</c> and <c>ResourcePrefix</c> settings, resolving the assembly from the
    /// current app domain.
    /// </summary>
    /// <param name="config">The collection configuration carrying the required settings.</param>
    /// <returns>An observable that emits the constructed stream provider.</returns>
    public IObservable<IStreamProvider> Create(ContentCollectionConfig config)
    {
        var assemblyName = config.Settings?.GetValueOrDefault("AssemblyName")
            ?? throw new ArgumentException("AssemblyName required for EmbeddedResource");
        var resourcePrefix = config.Settings?.GetValueOrDefault("ResourcePrefix")
            ?? throw new ArgumentException("ResourcePrefix required for EmbeddedResource");

        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == assemblyName)
            ?? throw new InvalidOperationException($"Assembly not found: {assemblyName}");

        return Observable.Return<IStreamProvider>(new EmbeddedResourceStreamProvider(assembly, resourcePrefix));
    }
}
