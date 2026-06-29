namespace MeshWeaver.ContentCollections;

/// <summary>
/// Factory for creating stream providers from configuration. Reactive surface —
/// implementations compose hub round-trips / I/O via <see cref="IObservable{T}"/>
/// without bridging to <see cref="System.Threading.Tasks.Task"/>; consumers
/// subscribe via <c>SelectMany</c>/<c>Subscribe</c>.
/// </summary>
public interface IStreamProviderFactory
{
    /// <summary>
    /// Creates a stream provider from the given configuration. Emits exactly one
    /// <see cref="IStreamProvider"/> and completes; failures flow via <c>OnError</c>.
    /// </summary>
    IObservable<IStreamProvider> Create(ContentCollectionConfig config);
}
