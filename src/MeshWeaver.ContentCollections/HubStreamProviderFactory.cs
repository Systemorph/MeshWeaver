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

    public IStreamProvider Create(Dictionary<string, string>? configuration)
    {
        if (configuration == null)
            throw new ArgumentException("Configuration is required for Hub source type");

        if (!configuration.TryGetValue("Address", out var address))
            throw new ArgumentException("Address is required in configuration for Hub source type");

        if (!configuration.TryGetValue("CollectionName", out var collectionName))
            throw new ArgumentException("CollectionName is required in configuration for Hub source type");

        // Query the remote hub for the collection configuration
        var response = hub.AwaitResponse(
            new GetContentCollectionRequest([collectionName]),
            o => o.WithTarget(address)
        ).GetAwaiter().GetResult();

        var remoteConfig = response.Message.Collections.FirstOrDefault();
        if (remoteConfig == null)
            throw new InvalidOperationException($"Collection '{collectionName}' not found at address '{address}'");

        // Get the factory for the remote collection's source type
        var factory = hub.ServiceProvider.GetKeyedService<IStreamProviderFactory>(remoteConfig.SourceType);
        if (factory == null)
            throw new InvalidOperationException($"Unknown provider type '{remoteConfig.SourceType}'");

        // Build configuration dictionary from remote config
        var remoteConfiguration = remoteConfig.Settings ?? new Dictionary<string, string>();
        if (remoteConfig.BasePath != null && !remoteConfiguration.ContainsKey("BasePath"))
        {
            remoteConfiguration["BasePath"] = remoteConfig.BasePath;
        }

        // Create provider using the factory
        return factory.Create(remoteConfiguration);
    }
}
