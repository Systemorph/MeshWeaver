using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Factory for creating content collections that are loaded from remote hubs.
/// The collection configuration is retrieved by querying the Address.
/// </summary>
public class HubContentCollectionFactory : IContentCollectionFactory
{
    public const string SourceType = "Hub";

    public async Task<ContentCollection> CreateAsync(ContentCollectionConfig config, IMessageHub hub, CancellationToken cancellationToken = default)
    {
        if (config.Address == null)
            throw new ArgumentException("Address is required for Hub source type", nameof(config));

        // Query the remote hub for the collection configuration
        var response = await hub.AwaitResponse(
            new GetContentCollectionRequest([config.Name!]),
            o => o.WithTarget(config.Address),
            cancellationToken
        );

        var remoteConfig = response.Message.Collections.FirstOrDefault();
        if (remoteConfig == null)
            throw new InvalidOperationException($"Collection '{config.Name}' not found at address '{config.Address}'");

        // Get the factory for the remote collection's source type
        var factory = hub.ServiceProvider.GetKeyedService<IStreamProviderFactory>(remoteConfig.SourceType);
        if (factory == null)
            throw new InvalidOperationException($"Unknown provider type '{remoteConfig.SourceType}'");

        // Build configuration dictionary
        var configuration = remoteConfig.Settings ?? new Dictionary<string, string>();
        if (remoteConfig.BasePath != null && !configuration.ContainsKey("BasePath"))
        {
            configuration["BasePath"] = remoteConfig.BasePath;
        }

        // Create provider using the factory
        var provider = factory.Create(configuration);

        // Create and initialize the collection
        var collection = new ContentCollection(remoteConfig, provider, hub);
        await collection.InitializeAsync(cancellationToken);

        return collection;
    }
}
