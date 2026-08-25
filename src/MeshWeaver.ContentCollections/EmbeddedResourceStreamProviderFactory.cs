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
            ?? throw Missing(config, "AssemblyName");
        var resourcePrefix = config.Settings?.GetValueOrDefault("ResourcePrefix")
            ?? throw Missing(config, "ResourcePrefix");

        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == assemblyName)
            ?? throw new InvalidOperationException($"Assembly not found: {assemblyName}");

        return Observable.Return<IStreamProvider>(new EmbeddedResourceStreamProvider(assembly, resourcePrefix));
    }

    /// <summary>
    /// The guard's exception, naming WHICH collection is missing the setting and where it came
    /// from — not just which field is absent.
    ///
    /// <para>Every registration site supplies both settings
    /// (<c>AddEmbeddedResourceContentCollection</c>), so a config that reaches here without them
    /// did not come from a registration: it was rebuilt somewhere in between and lost them. The
    /// original message named only the field, so triaging issues #2122/#2123 meant walking the
    /// stack backwards to guess which collection and which hub — the answer was the wire
    /// projection in <c>ContentFileResolver.ReadCollectionConfigs</c>, three frames up and in
    /// another assembly. Naming the collection and its address makes the next one a one-line
    /// read.</para>
    /// </summary>
    /// <param name="config">The config that arrived without the setting.</param>
    /// <param name="setting">The missing setting's key.</param>
    /// <returns>The exception to throw.</returns>
    private static ArgumentException Missing(ContentCollectionConfig config, string setting) =>
        new($"{setting} required for EmbeddedResource collection '{config.Name}'"
            + (config.Address is null ? "" : $" at '{config.Address}'")
            + ". Every registration supplies it, so a config without it was rebuilt in transit "
            + "and lost its Settings.");
}
