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
    /// <returns>The reactive stream which can be subscribed to.</returns>
    Task RegisterStreamAsync(Address address, AsyncDelivery callback);

    /// <summary>
    /// <summary>
    /// Unregisters the corresponding address from the routing service. This method must be called by submitting an <see cref="UnregisterAddressRequest"/> to MeshAddress.
    /// </summary>
    /// </summary>
    /// <param name="address">Address to be unregistered</param>
    /// <returns>Routed address if it was registered. This can be used, e.g. for cascading disposal.</returns>
    Task Async(Address address);
    /// <summary>
    /// Stream Namespace for incoming messages
    /// </summary>
    public const string MessageIn = nameof(MessageIn);
}
