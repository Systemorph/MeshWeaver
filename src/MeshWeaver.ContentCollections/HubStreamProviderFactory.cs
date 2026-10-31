using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
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
    /// <summary>The source-type discriminator for hub-backed (remote) collections.</summary>
    public const string SourceType = "Hub";

    /// <summary>
    /// Queries the remote hub at the config's address for the collection's configuration, then
    /// delegates to the stream-provider factory matching the remote collection's source type.
    /// </summary>
    /// <param name="config">The collection configuration; its <c>Address</c> must be set.</param>
    /// <returns>An observable that emits the stream provider resolved from the remote collection.</returns>
    public IObservable<IStreamProvider> Create(ContentCollectionConfig config)
        => Create(config, ImmutableList<string>.Empty);

    /// <summary>
    /// One hop of the resolution. <paramref name="chain"/> holds every <c>{address}/{collection}</c>
    /// this resolution has already asked, in order.
    /// </summary>
    /// <param name="config">The hop's hub-backed collection configuration.</param>
    /// <param name="chain">The hops already asked by this resolution, in order.</param>
    /// <remarks>
    /// 🚨 A hub-backed collection may answer with ANOTHER hub-backed config, and that answer is
    /// resolved by this same factory. Without the chain, a config that leads back to a hop already
    /// asked — most simply a node with no collection of that name of its own, whose content service
    /// falls back to a parent that holds a <c>Hub</c> config naming that very node — loops forever:
    /// every round is one more asynchronous request, so nothing fails while it runs, and every round
    /// nests one more <c>SelectMany</c> inner observer. The first terminal signal then unwinds through
    /// all of them on one stack. On a production silo that was 130,840 nested
    /// <c>SelectMany</c> inner observers carrying one <c>OnError</c> (a <c>ShuttingDown</c> answer from
    /// the quiescing owner), a <see cref="StackOverflowException"/>, and a process frozen for 42 s by
    /// its crash dump (MeshWeaver#5388). A cycle has no answer, so it is refused by name on the hop
    /// that closes it.
    /// </remarks>
    private IObservable<IStreamProvider> Create(ContentCollectionConfig config, ImmutableList<string> chain)
    {
        if (config.Address == null)
            throw new ArgumentException("Address is required for Hub source type");

        var collectionName = config.Settings?.GetValueOrDefault("CollectionName") ?? config.Name;
        var hop = $"{config.Address}/{collectionName}";
        if (chain.Contains(hop, StringComparer.OrdinalIgnoreCase))
            return Observable.Throw<IStreamProvider>(new HubCollectionCycleException(chain.Add(hop)));
        chain = chain.Add(hop);

        // Query the remote hub for the collection configuration via GetDataRequest+ContentCollectionReference.
        // 🚨 Canonical observe-BEFORE-post (hub.Observe(request, …) pre-registers the response
        // callback by message-id, then posts on Subscribe). The previous post-then-Observe(delivery)
        // shape raced: the Post fired at Create() call time while the callback only registered at
        // subscription — a fast response landed unobserved and the observable NEVER emitted, which
        // a promise-cached collection entry then pins forever (silence, not an error).
        //
        // 🚨 ISSUED OFF THE ROUTER (#5620). This factory is keyed-scoped, so resolved from the ROOT
        // container its hub IS the mesh router — the exact shape of #5620's production pair
        // (`GetDataRequest … sender: mesh/{id}` / `GetDataResponse … target: mesh/{id}`). A one-shot
        // config read belongs on ReadIssuingHub(), the identity function for every other hub.
        return hub.ReadIssuingHub().Observe(
                new GetDataRequest(new ContentCollectionReference([collectionName])),
                o => o.WithTarget(config.Address))
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

                // The next hop of a hub-backed chain stays on THIS resolution, so it carries the
                // chain; every other source type ends the chain.
                if (string.Equals(remoteConfig.SourceType, SourceType, StringComparison.Ordinal))
                    return Create(remoteConfig, chain);

                var factory = hub.ServiceProvider.GetKeyedService<IStreamProviderFactory>(remoteConfig.SourceType);
                if (factory == null)
                    return Observable.Throw<IStreamProvider>(new InvalidOperationException(
                        $"Unknown provider type '{remoteConfig.SourceType}'"));

                return factory.Create(remoteConfig);
            });
    }
}

/// <summary>
/// A hub-backed content collection whose resolution leads back to a hop it already asked. A cycle
/// has no answer: resolving it would ask forever, so it is refused on the hop that closes it.
/// </summary>
/// <param name="chain">Every <c>{address}/{collection}</c> asked, in order, ending with the repeat.</param>
public sealed class HubCollectionCycleException(IReadOnlyList<string> chain)
    : InvalidOperationException(
        "Hub-backed content collection resolves in a cycle: " + string.Join(" -> ", chain)
        + ". A node without a collection of this name answers with the config it inherits; when "
        + "that config is a Hub source naming a hop already on the chain, nothing can ever serve it.")
{
    /// <summary>Every <c>{address}/{collection}</c> asked, in order, ending with the repeat.</summary>
    public IReadOnlyList<string> Chain { get; } = chain;
}
