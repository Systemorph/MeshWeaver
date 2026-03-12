using MeshWeaver.Data;
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

    public async Task<IStreamProvider> CreateAsync(ContentCollectionConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Address == null)
            throw new ArgumentException("Address is required for Hub source type");

        var collectionName = config.Settings?.GetValueOrDefault("CollectionName") ?? config.Name;

        // Query the remote hub for the collection configuration using GetDataRequest with ContentCollectionReference
        var delivery = hub.Post(
            new GetDataRequest(new ContentCollectionReference([collectionName])),
            o => o.WithTarget(config.Address))!;
        var callbackResponse = await hub.RegisterCallback(delivery, (d, _) => Task.FromResult(d), cancellationToken);
        var responseMsg = ((IMessageDelivery<GetDataResponse>)callbackResponse).Message;

        // Response data contains collection configurations
        var configs = responseMsg.Data as IReadOnlyCollection<ContentCollectionConfig>;
        var remoteConfig = configs?.FirstOrDefault();
        if (remoteConfig == null)
            throw new InvalidOperationException($"Collection '{collectionName}' not found at address '{config.Address}'");

        // Get the factory for the remote collection's source type
        var factory = hub.ServiceProvider.GetKeyedService<IStreamProviderFactory>(remoteConfig.SourceType);
        if (factory == null)
            throw new InvalidOperationException($"Unknown provider type '{remoteConfig.SourceType}'");

        // Create provider using the factory with the remote config (now properly async)
        return await factory.CreateAsync(remoteConfig, cancellationToken);
    }
}
