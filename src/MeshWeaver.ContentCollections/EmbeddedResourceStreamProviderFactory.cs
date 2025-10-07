namespace MeshWeaver.ContentCollections;

/// <summary>
/// Factory for creating EmbeddedResourceStreamProvider instances
/// </summary>
public class EmbeddedResourceStreamProviderFactory : IStreamProviderFactory
{
    public IStreamProvider Create(Dictionary<string, string>? configuration)
    {
        var assemblyName = configuration?.GetValueOrDefault("AssemblyName")
            ?? throw new ArgumentException("AssemblyName required for EmbeddedResource");
        var resourcePrefix = configuration?.GetValueOrDefault("ResourcePrefix")
            ?? throw new ArgumentException("ResourcePrefix required for EmbeddedResource");

        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == assemblyName)
            ?? throw new InvalidOperationException($"Assembly not found: {assemblyName}");

        return new EmbeddedResourceStreamProvider(assembly, resourcePrefix);
    }
}
