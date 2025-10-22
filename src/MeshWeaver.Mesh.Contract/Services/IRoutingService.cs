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
    /// Registers addressType and id and gets a stream
    /// </summary>
    /// <param name="address">Address to be registered for streaming</param>
    /// <param name="callback">Callback to deliver messages from the stream.</param>
    /// <returns>An async disposable which will unsubscribe when called.</returns>
    Task<IAsyncDisposable> RegisterStreamAsync(Address address, AsyncDelivery callback);

    /// <summary>
    /// Registers addressType and id and gets a stream
    /// </summary>
    /// <param name="address">Address to be registered for streaming</param>
    /// <param name="callback">Callback to deliver messages from the stream.</param>
    /// <returns>An async disposable which will unsubscribe when called.</returns>
    Task<IAsyncDisposable> RegisterStreamAsync(Address address, SyncDelivery callback)
        => RegisterStreamAsync(address, (d, _) => Task.FromResult(callback(d)));

    /// <summary>
    /// Easy access overload to register message hubs.
    /// </summary>
    /// <param name="hub">Hub to be exposed to the web.</param>
    /// <returns></returns>
    Task<IAsyncDisposable> RegisterStreamAsync(IMessageHub hub)
        => RegisterStreamAsync(hub.Address, hub.DeliverMessage);

    /// <summary>
    /// Stream Namespace for incoming messages
    /// </summary>
    public const string MessageIn = nameof(MessageIn);

}
