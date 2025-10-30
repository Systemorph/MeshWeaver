namespace MeshWeaver.ContentCollections;

/// <summary>
/// Factory for creating EmbeddedResourceStreamProvider instances
/// </summary>
public class EmbeddedResourceStreamProviderFactory : IStreamProviderFactory
{
    public Task<IStreamProvider> CreateAsync(ContentCollectionConfig config, CancellationToken cancellationToken = default)
    {
        var assemblyName = config.Settings?.GetValueOrDefault("AssemblyName")
            ?? throw new ArgumentException("AssemblyName required for EmbeddedResource");
        var resourcePrefix = config.Settings?.GetValueOrDefault("ResourcePrefix")
            ?? throw new ArgumentException("ResourcePrefix required for EmbeddedResource");

        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == assemblyName)
            ?? throw new InvalidOperationException($"Assembly not found: {assemblyName}");

        return Task.FromResult<IStreamProvider>(new EmbeddedResourceStreamProvider(assembly, resourcePrefix));
    }
}
