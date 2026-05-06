using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Main routing in the mesh.
/// </summary>
public interface IRoutingService
{
    /// <summary>
    /// Routes the delivery in the mesh. 
    /// </summary>
    /// <param name="delivery"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IMessageDelivery> DeliverMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken = default);


    /// <summary>
    /// Registers a stream for the given address and returns immediately.
    /// The returned <see cref="IAsyncDisposable"/> unsubscribes on DisposeAsync —
    /// implementations that need genuinely-async subscription work (e.g. Orleans
    /// memory-stream <c>SubscribeAsync</c>) fire it in the background and capture
    /// the handle for clean unsubscribe.
    /// </summary>
    /// <param name="address">Address to be registered for streaming.</param>
    /// <param name="callback">Callback to deliver messages from the stream.</param>
    /// <returns>An async disposable which will unsubscribe when called.</returns>
    IAsyncDisposable RegisterStream(Address address, AsyncDelivery callback);

    /// <summary>
    /// Registers a stream for the given address and returns immediately.
    /// </summary>
    IAsyncDisposable RegisterStream(Address address, SyncDelivery callback)
        => RegisterStream(address, (d, _) => Task.FromResult(callback(d)));

    /// <summary>
    /// Easy-access overload to register a hub as a stream sink.
    /// </summary>
    IAsyncDisposable RegisterStream(IMessageHub hub)
        => RegisterStream(hub.Address, hub.DeliverMessage);

    /// <summary>
    /// Stream Namespace for incoming messages
    /// </summary>
    public const string MessageIn = nameof(MessageIn);

}
