using System.Reflection;
using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

public class EmbeddedResourceContentCollectionFactory : IContentCollectionFactory
{
    public const string SourceType = "EmbeddedResource";

    public async Task<ContentCollection> CreateAsync(ContentCollectionConfig config, IMessageHub hub, CancellationToken cancellationToken = default)
    {
        // Get assembly and resource prefix from settings
        var settings = config.Settings ?? throw new ArgumentException("Settings are required for EmbeddedResource source type", nameof(config));

        if (!settings.TryGetValue("AssemblyName", out var assemblyName))
            throw new ArgumentException("AssemblyName is required in settings for EmbeddedResource source type", nameof(config));

        if (!settings.TryGetValue("ResourcePrefix", out var resourcePrefix))
            throw new ArgumentException("ResourcePrefix is required in settings for EmbeddedResource source type", nameof(config));

        // Load the assembly
        var assembly = Assembly.Load(assemblyName);

        // Create the stream provider
        var provider = new EmbeddedResourceStreamProvider(assembly, resourcePrefix);

        // Create and initialize the collection
        var collection = new ContentCollection(config, provider, hub);
        await collection.InitializeAsync(cancellationToken);

        return collection;
    }
}
