using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Factory for creating <see cref="IStreamProvider"/> instances backed by a remote
/// mesh hub. Pure reactive — composes <c>hub.Observe</c> + the resolved factory's
/// <c>Create</c> via <see cref="Observable.SelectMany{T,TResult}(IObservable{T}, Func{T, IObservable{TResult}})"/>;
/// no <c>await</c>, no <c>FirstAsync</c>, no <c>ToTask</c>.
/// </summary>
public class HubStreamProviderFactory(IMessageHub hub) : IStreamProviderFactory
{
    public const string SourceType = "Hub";

    public IObservable<IStreamProvider> Create(ContentCollectionConfig config)
    {
        if (config.Address == null)
            throw new ArgumentException("Address is required for Hub source type");

        var collectionName = config.Settings?.GetValueOrDefault("CollectionName") ?? config.Name;

        // Query the remote hub for the collection configuration via GetDataRequest+ContentCollectionReference.
        var delivery = hub.Post(
            new GetDataRequest(new ContentCollectionReference([collectionName])),
            o => o.WithTarget(config.Address))!;

        return hub.Observe(delivery)
            .SelectMany(callbackResponse =>
            {
                if (callbackResponse.Message is not GetDataResponse responseMsg)
                    return Observable.Throw<IStreamProvider>(new InvalidOperationException(
                        $"Unexpected response shape '{callbackResponse.Message?.GetType().Name ?? "null"}' " +
                        $"when querying collection '{collectionName}' at address '{config.Address}'."));

                var configs = responseMsg.Data as IReadOnlyCollection<ContentCollectionConfig>;
                var remoteConfig = configs?.FirstOrDefault();
                if (remoteConfig == null)
                    return Observable.Throw<IStreamProvider>(new InvalidOperationException(
                        $"Collection '{collectionName}' not found at address '{config.Address}'"));

                var factory = hub.ServiceProvider.GetKeyedService<IStreamProviderFactory>(remoteConfig.SourceType);
                if (factory == null)
                    return Observable.Throw<IStreamProvider>(new InvalidOperationException(
                        $"Unknown provider type '{remoteConfig.SourceType}'"));

                return factory.Create(remoteConfig);
            });
    }
}
