using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Factory for creating HubStreamProvider instances that load content from remote hubs.
/// The stream provider is retrieved by querying the Address for its collection configuration.
/// </summary>
public class HubStreamProviderFactory(IMessageHub hub) : IStreamProviderFactory
{
    public const string SourceType = "Hub";

    public IStreamProvider Create(ContentCollectionConfig config)
    {
        if (config.Address == null)
            throw new ArgumentException("Address is required for Hub source type");

        var collectionName = config.Settings?.GetValueOrDefault("CollectionName") ?? config.Name;

        // Query the remote hub for the collection configuration
        var response = hub.AwaitResponse(
            new GetContentCollectionRequest([collectionName]),
            o => o.WithTarget(config.Address)
        ).GetAwaiter().GetResult();

        var remoteConfig = response.Message.Collections.FirstOrDefault();
        if (remoteConfig == null)
            throw new InvalidOperationException($"Collection '{collectionName}' not found at address '{config.Address}'");

        // Get the factory for the remote collection's source type
        var factory = hub.ServiceProvider.GetKeyedService<IStreamProviderFactory>(remoteConfig.SourceType);
        if (factory == null)
            throw new InvalidOperationException($"Unknown provider type '{remoteConfig.SourceType}'");

        // Create provider using the factory with the remote config
        return factory.Create(remoteConfig);
    }
}
